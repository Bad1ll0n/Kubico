import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'design/components.dart';
import 'theme.dart';

/// Despesas partilhadas (F4-10): quem pagou o quê, o saldo de cada um e quem paga a quem para ficarem acertados.
class ExpensesView extends StatefulWidget {
  const ExpensesView({required this.household, super.key});

  final HouseholdSummary household;

  @override
  State<ExpensesView> createState() => _ExpensesViewState();
}

class _ExpensesViewState extends State<ExpensesView> {
  ExpensesOverview? _overview;
  HouseholdDetails? _details;
  Object? _error;
  StreamSubscription<HouseholdEvent>? _events;
  late final _reload = CoalescedReload(_load);

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;
  ModuleAccess get _access => widget.household.accessTo(HouseholdModule.expenses);
  String? get _me => context.read<SessionController>().user?.id;

  @override
  void initState() {
    super.initState();
    _events = context.read<HouseholdRealtime>().events.listen((event) {
      if (event is ExpensesChanged) _reload.request();
    });
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    _reload.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final results = await Future.wait<Object>([_api.getExpenses(_householdId), _api.getHousehold(_householdId)]);
      if (!mounted) return;
      setState(() {
        _overview = results[0] as ExpensesOverview;
        _details = results[1] as HouseholdDetails;
        _error = null;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Widget _avatar(String userId) {
    final member = _details?.member(userId);
    return member == null
        ? const Icon(Icons.person_rounded)
        : MemberAvatar(name: member.displayName, color: member.color, emoji: member.avatar, size: 32);
  }

  String _name(String userId) => _details?.member(userId)?.displayName ?? 'Antigo membro'.tr;

  Future<void> _edit([Expense? expense]) async {
    final details = _details;
    if (details == null) return;
    final draft = await showModalBottomSheet<ExpenseDraft>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _ExpenseEditor(
        members: [
          for (final m in details.members)
            if (!m.isChildProfile) m,
        ],
        me: _me,
        expense: expense,
      ),
    );
    if (draft == null || !mounted) return;
    try {
      await _api.saveExpense(_householdId, draft, expenseId: expense?.id);
      HapticFeedback.selectionClick();
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _settle(ExpenseTransfer t) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Registar o acerto?'.tr),
        content: Text('${_name(t.fromUserId)} pagou ${formatEuros(t.amountCents)} a ${_name(t.toUserId)}.'.tr),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Cancelar'.tr)),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text('Registar'.tr)),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    try {
      await _api.createSettlement(_householdId, t, DateUtils.dateOnly(clock.now()));
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final overview = _overview;
    if (overview == null) {
      return _error == null
          ? const Center(child: CircularProgressIndicator())
          : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load);
    }
    final theme = Theme.of(context);
    final canAdd = _access.canParticipate;
    return Scaffold(
      floatingActionButton: canAdd
          ? FloatingActionButton.extended(
              onPressed: _edit,
              icon: const Icon(Icons.add_rounded),
              label: Text('Nova despesa'.tr),
            )
          : null,
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: readablePadding(context),
          children: [
            SectionHeader('Saldos'.tr),
            if (overview.balances.isEmpty)
              Padding(
                padding: const EdgeInsets.all(8),
                child: Text('Está tudo acertado.'.tr, style: theme.textTheme.bodyLarge),
              ),
            for (final b in overview.balances)
              ListTile(
                leading: _avatar(b.userId),
                title: Text(_name(b.userId)),
                trailing: Text(
                  b.netCents > 0 ? 'recebe ${formatEuros(b.netCents)}'.tr : 'deve ${formatEuros(-b.netCents)}'.tr,
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: b.netCents > 0 ? context.kubico.success : context.kubico.warning,
                    fontFeatures: tabularFigures,
                  ),
                ),
              ),
            if (overview.transfers.isNotEmpty) ...[
              SectionHeader('Para acertar'.tr),
              for (final t in overview.transfers)
                Card(
                  child: ListTile(
                    leading: const Icon(Icons.swap_horiz_rounded),
                    title: Text('${_name(t.fromUserId)} → ${_name(t.toUserId)}'),
                    subtitle: Text(formatEuros(t.amountCents)),
                    trailing: canAdd ? TextButton(onPressed: () => _settle(t), child: Text('Acertar'.tr)) : null,
                  ),
                ),
            ],
            SectionHeader('Despesas'.tr),
            if (overview.expenses.isEmpty)
              StateView(
                inList: true,
                icon: Icons.receipt_long_rounded,
                title: 'Ainda não há despesas'.tr,
                message: canAdd ? 'Regista quem pagou o quê e a app diz quem deve a quem.'.tr : null,
              ),
            for (final e in overview.expenses)
              Card(
                child: ListTile(
                  leading: const Icon(Icons.receipt_long_rounded),
                  title: Text(e.description),
                  subtitle: Text('Pagou ${_name(e.paidByUserId)} · ${e.date.day}/${e.date.month}'.tr),
                  trailing: Text(formatEuros(e.amountCents), style: const TextStyle(fontFeatures: tabularFigures)),
                  onTap: _access.canManage || (canAdd && e.createdByUserId == _me) ? () => _edit(e) : null,
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Registar ou mudar uma despesa: descrição, valor, quem pagou e entre quem se divide (em partes iguais).
class _ExpenseEditor extends StatefulWidget {
  const _ExpenseEditor({required this.members, required this.me, this.expense});

  final List<Member> members;
  final String? me;
  final Expense? expense;

  @override
  State<_ExpenseEditor> createState() => _ExpenseEditorState();
}

class _ExpenseEditorState extends State<_ExpenseEditor> {
  late final _description = TextEditingController(text: widget.expense?.description ?? '');
  late final _amount = TextEditingController(
    text: widget.expense == null ? '' : (widget.expense!.amountCents / 100).toStringAsFixed(2).replaceAll('.', ','),
  );
  late String? _paidBy = widget.expense?.paidByUserId ?? widget.me ?? widget.members.firstOrNull?.userId;
  late final Set<String> _split = {
    ...?widget.expense?.shares.map((s) => s.userId),
    if (widget.expense == null) ...widget.members.map((m) => m.userId),
  };
  String? _error;

  @override
  void dispose() {
    _description.dispose();
    _amount.dispose();
    super.dispose();
  }

  void _save() {
    final cents = parseEuros(_amount.text);
    if (_description.text.trim().isEmpty || cents == null || cents <= 0 || _paidBy == null || _split.isEmpty) {
      setState(() => _error = 'Escreve a descrição, o valor e entre quem se divide.'.tr);
      return;
    }
    Navigator.pop(
      context,
      ExpenseDraft(
        description: _description.text.trim(),
        amountCents: cents,
        paidByUserId: _paidBy!,
        date: widget.expense?.date ?? DateUtils.dateOnly(clock.now()),
        splitWith: [
          for (final m in widget.members)
            if (_split.contains(m.userId)) m.userId,
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.fromLTRB(24, 0, 24, 24 + MediaQuery.viewInsetsOf(context).bottom),
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              widget.expense == null ? 'Nova despesa'.tr : 'Mudar despesa'.tr,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _description,
              autofocus: widget.expense == null,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'O quê?'.tr, hintText: 'Supermercado, luz, gás…'.tr),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _amount,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(labelText: 'Valor (€)'.tr, errorText: _error),
            ),
            const SizedBox(height: 12),
            DropdownButtonFormField<String>(
              initialValue: widget.members.any((m) => m.userId == _paidBy) ? _paidBy : null,
              decoration: InputDecoration(labelText: 'Quem pagou'.tr),
              items: [for (final m in widget.members) DropdownMenuItem(value: m.userId, child: Text(m.displayName))],
              onChanged: (v) => setState(() => _paidBy = v),
            ),
            const SizedBox(height: 12),
            Text('Dividir por'.tr, style: Theme.of(context).textTheme.titleSmall),
            Wrap(
              spacing: 8,
              runSpacing: 4,
              children: [
                for (final m in widget.members)
                  FilterChip(
                    avatar: MemberAvatar(name: m.displayName, color: m.color, emoji: m.avatar, size: 24),
                    label: Text(m.displayName),
                    selected: _split.contains(m.userId),
                    onSelected: (on) => setState(() => on ? _split.add(m.userId) : _split.remove(m.userId)),
                  ),
              ],
            ),
            const SizedBox(height: 16),
            FilledButton(onPressed: _save, child: Text('Guardar'.tr)),
          ],
        ),
      ),
    );
  }
}
