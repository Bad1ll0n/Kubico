import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// A mesada, no ecrã das recompensas: quem gere as recompensas define-a e marca os períodos como pagos; cada
/// membro vê a sua (quanto vai ganhar esta semana ou este mês, e o que já recebeu).
class AllowanceSection extends StatefulWidget {
  const AllowanceSection({super.key, required this.household, required this.members});

  final HouseholdSummary household;

  /// Os membros da casa (nomes e cores), para escolher a quem dar mesada.
  final List<MemberPoints> members;

  @override
  State<AllowanceSection> createState() => _AllowanceSectionState();
}

class _AllowanceSectionState extends State<AllowanceSection> {
  final _subscriptions = <StreamSubscription<Object?>>[];
  List<Allowance>? _allowances;

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;
  bool get _canManage => widget.household.accessTo(HouseholdModule.rewards).canManage;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _subscriptions
      ..add(
        realtime.events.listen((event) {
          // Uma tarefa feita (ou não aceite) muda quanto se ganha neste período.
          if (event is AllowanceChanged || event is ChoreUpserted) _load();
        }),
      )
      ..add(realtime.reconnected.listen((_) => _load()));
    _load();
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final allowances = await _api.getAllowances(_householdId);
      if (mounted) setState(() => _allowances = allowances);
    } catch (_) {
      // A mesada é secundária neste ecrã; fica a última que se leu.
    }
  }

  MemberPoints? _member(String userId) => widget.members.where((m) => m.userId == userId).firstOrNull;

  Future<void> _run(Future<void> Function() action) async {
    try {
      await action();
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _edit([Allowance? allowance]) async {
    final without = [
      for (final m in widget.members)
        if (!(_allowances ?? const []).any((a) => a.userId == m.userId)) m,
    ];
    final result = await showDialog<_AllowanceEdit>(
      context: context,
      builder: (_) =>
          _AllowanceDialog(allowance: allowance, member: _member(allowance?.userId ?? ''), choices: without),
    );
    if (result == null) return;
    await _run(() async {
      if (result.stop) {
        await _api.stopAllowance(_householdId, result.userId);
      } else {
        await _api.saveAllowance(
          _householdId,
          result.userId,
          amountCents: result.amountCents,
          bonusPerChoreCents: result.bonusCents,
          period: result.period,
        );
      }
    });
  }

  Future<void> _pay(Allowance allowance, AllowancePeriodEntry period) async {
    final name = _member(allowance.userId)?.displayName ?? 'esta pessoa';
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Pagar ${formatEuros(period.amountCents)} a $name?'),
        content: Text(
          [
            '${capitalize(_periodLabel(allowance.period, period))}: ${_detail(period)}.',
            if (period.isCurrent)
              'Ainda não acabou: as tarefas feitas depois de pagar já não contam para ${_periodLabel(allowance.period, period)}.',
            'Fica marcado como pago e $name recebe um aviso.',
          ].join(' '),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancelar')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Pagar')),
        ],
      ),
    );
    if (confirmed != true) return;
    await _run(() => _api.payAllowance(_householdId, allowance.userId, period.start));
    if (mounted) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text('Mesada de $name marcada como paga.')));
    }
  }

  @override
  Widget build(BuildContext context) {
    final allowances = _allowances;
    if (allowances == null || (allowances.isEmpty && !_canManage)) return const SizedBox.shrink();
    final theme = Theme.of(context);
    final someoneWithout = widget.members.any((m) => !allowances.any((a) => a.userId == m.userId));
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(8, 16, 0, 4),
          child: Row(
            children: [
              Expanded(child: Text('Mesada', style: theme.textTheme.titleSmall)),
              if (_canManage && someoneWithout)
                TextButton.icon(
                  onPressed: _edit,
                  icon: const Icon(Icons.add, size: 18),
                  label: const Text('Dar mesada'),
                ),
            ],
          ),
        ),
        if (allowances.isEmpty)
          const Padding(
            padding: EdgeInsets.fromLTRB(8, 0, 8, 8),
            child: Text('Ninguém tem mesada. Podes dar um valor por semana ou por mês, com um extra por tarefa feita.'),
          ),
        for (final allowance in allowances) _card(allowance),
      ],
    );
  }

  Widget _card(Allowance allowance) {
    final theme = Theme.of(context);
    final member = _member(allowance.userId);
    final name = member?.displayName ?? 'Antigo membro';
    // O período atual, os que estão por pagar e os últimos três pagos.
    final shown = [
      ...allowance.periods.where((p) => p.isCurrent || !p.isPaid),
      ...allowance.periods.where((p) => !p.isCurrent && p.isPaid).take(3),
    ];
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 8, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                CircleAvatar(
                  radius: 14,
                  backgroundColor: member == null ? null : colorFromHex(member.color),
                  foregroundColor: Colors.white,
                  child: Text(name.isEmpty ? '?' : name[0].toUpperCase(), style: const TextStyle(fontSize: 12)),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(name, style: theme.textTheme.titleMedium),
                      Text(allowance.describe(), style: theme.textTheme.bodySmall),
                    ],
                  ),
                ),
                if (_canManage)
                  IconButton(
                    tooltip: 'Mudar a mesada de $name',
                    onPressed: () => _edit(allowance),
                    icon: const Icon(Icons.edit_outlined),
                  ),
              ],
            ),
            if (allowance.owedCents > 0)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(
                  'Por pagar: ${formatEuros(allowance.owedCents)}',
                  style: theme.textTheme.titleSmall?.copyWith(color: theme.colorScheme.primary),
                ),
              ),
            for (final period in shown)
              ListTile(
                dense: true,
                contentPadding: const EdgeInsets.only(left: 0, right: 0),
                title: Text(capitalize(_periodLabel(allowance.period, period))),
                subtitle: Text(_detail(period)),
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(formatEuros(period.amountCents), style: theme.textTheme.titleSmall),
                    const SizedBox(width: 8),
                    if (period.isPaid)
                      _canManage
                          ? PopupMenuButton<String>(
                              tooltip: 'Opções do pagamento',
                              onSelected: (_) => _run(
                                () => _api.undoAllowancePayment(_householdId, allowance.userId, period.paymentId!),
                              ),
                              itemBuilder: (_) => const [
                                PopupMenuItem(value: 'undo', child: Text('Afinal não foi pago')),
                              ],
                              child: const Chip(label: Text('Pago'), visualDensity: VisualDensity.compact),
                            )
                          : const Chip(label: Text('Pago'), visualDensity: VisualDensity.compact)
                    else if (_canManage)
                      FilledButton.tonal(onPressed: () => _pay(allowance, period), child: const Text('Pagar')),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }

  static String _detail(AllowancePeriodEntry period) => [
    switch (period.choresDone) {
      0 => 'nenhuma tarefa feita',
      1 => '1 tarefa feita',
      final n => '$n tarefas feitas',
    },
    if (period.isCurrent && !period.isPaid) 'ainda a decorrer',
  ].join(' · ');

  static String _periodLabel(AllowancePeriod kind, AllowancePeriodEntry period) => switch (kind) {
    AllowancePeriod.weekly when period.isCurrent => 'esta semana',
    AllowancePeriod.weekly => 'semana de ${formatDayMonthShort(period.start)} a ${formatDayMonthShort(period.end)}',
    AllowancePeriod.monthly when period.isCurrent => 'este mês',
    AllowancePeriod.monthly => '${monthName(period.start.month)} de ${period.start.year}',
  };
}

/// O que o diálogo decidiu: guardar os valores, ou deixar de dar mesada ([stop]).
typedef _AllowanceEdit = ({String userId, int amountCents, int bonusCents, AllowancePeriod period, bool stop});

class _AllowanceDialog extends StatefulWidget {
  const _AllowanceDialog({this.allowance, this.member, required this.choices});

  final Allowance? allowance;
  final MemberPoints? member;

  /// A quem se pode dar mesada (quem ainda não tem), quando é nova.
  final List<MemberPoints> choices;

  @override
  State<_AllowanceDialog> createState() => _AllowanceDialogState();
}

class _AllowanceDialogState extends State<_AllowanceDialog> {
  late String? _userId = widget.allowance?.userId ?? widget.choices.firstOrNull?.userId;
  late AllowancePeriod _period = widget.allowance?.period ?? AllowancePeriod.weekly;
  late final _amount = TextEditingController(
    text: widget.allowance == null ? '' : formatEuros(widget.allowance!.amountCents, symbol: false),
  );
  late final _bonus = TextEditingController(
    text: (widget.allowance?.bonusPerChoreCents ?? 0) == 0
        ? ''
        : formatEuros(widget.allowance!.bonusPerChoreCents, symbol: false),
  );

  @override
  void dispose() {
    _amount.dispose();
    _bonus.dispose();
    super.dispose();
  }

  /// O valor (até 1000 €) e o extra por tarefa (até 100 €), em cêntimos; null se não for válido.
  int? get _amountCents => switch (parseEuros(_amount.text)) {
    final cents? when cents <= 100000 => cents,
    _ => null,
  };
  int? get _bonusCents => _bonus.text.trim().isEmpty
      ? 0
      : switch (parseEuros(_bonus.text)) {
          final cents? when cents <= 10000 => cents,
          _ => null,
        };
  bool get _valid => _userId != null && _amountCents != null && _bonusCents != null;

  void _save({bool stop = false}) => Navigator.pop<_AllowanceEdit>(context, (
    userId: _userId!,
    amountCents: _amountCents ?? 0,
    bonusCents: _bonusCents ?? 0,
    period: _period,
    stop: stop,
  ));

  @override
  Widget build(BuildContext context) {
    final editing = widget.allowance != null;
    return AlertDialog(
      title: Text(editing ? 'Mesada de ${widget.member?.displayName ?? ''}' : 'Dar mesada'),
      content: SizedBox(
        width: 400,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (!editing)
                DropdownButtonFormField<String>(
                  initialValue: _userId,
                  decoration: const InputDecoration(labelText: 'A quem'),
                  items: [
                    for (final m in widget.choices) DropdownMenuItem(value: m.userId, child: Text(m.displayName)),
                  ],
                  onChanged: (value) => setState(() => _userId = value),
                ),
              const SizedBox(height: 12),
              SegmentedButton<AllowancePeriod>(
                segments: const [
                  ButtonSegment(value: AllowancePeriod.weekly, label: Text('Por semana')),
                  ButtonSegment(value: AllowancePeriod.monthly, label: Text('Por mês')),
                ],
                selected: {_period},
                onSelectionChanged: (s) => setState(() => _period = s.single),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _amount,
                autofocus: !editing,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: InputDecoration(
                  labelText: 'Valor',
                  suffixText: '€',
                  hintText: '5,00',
                  errorText: _amount.text.trim().isNotEmpty && _amountCents == null ? 'Um valor até 1000 €.' : null,
                ),
                onChanged: (_) => setState(() {}),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _bonus,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: InputDecoration(
                  labelText: 'Extra por tarefa feita (opcional)',
                  suffixText: '€',
                  hintText: '0,50',
                  errorText: _bonusCents == null ? 'Um valor até 100 €.' : null,
                ),
                onChanged: (_) => setState(() {}),
              ),
              if (editing && widget.allowance!.period != _period)
                const Padding(
                  padding: EdgeInsets.only(top: 8),
                  child: Text('Ao mudar de semanal para mensal (ou o contrário), começa a contar a partir de agora.'),
                ),
            ],
          ),
        ),
      ),
      actions: [
        if (editing)
          TextButton(
            onPressed: () => _save(stop: true),
            style: TextButton.styleFrom(foregroundColor: Theme.of(context).colorScheme.error),
            child: const Text('Deixar de dar'),
          ),
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(onPressed: _valid ? _save : null, child: const Text('Guardar')),
      ],
    );
  }
}
