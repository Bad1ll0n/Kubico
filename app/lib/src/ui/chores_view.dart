import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'chore_editor.dart';
import 'chore_history_screen.dart';
import 'dates_pt.dart';
import 'theme.dart';

enum ChoreGroup {
  overdue('Em atraso'),
  today('Hoje'),
  thisWeek('Próximos 7 dias'),
  later('Mais tarde'),
  noDate('Sem prazo');

  const ChoreGroup(this._label);
  final String _label;

  String get label => _label.tr;
}

/// Em que secção da lista aparece uma tarefa, relativamente a [today].
ChoreGroup choreGroupFor(Chore chore, DateTime today) {
  final due = chore.dueDate;
  if (due == null) return ChoreGroup.noDate;
  final day = DateTime(due.year, due.month, due.day);
  final ref = DateTime(today.year, today.month, today.day);
  final diff = day.difference(ref).inDays;
  if (diff < 0) return ChoreGroup.overdue;
  if (diff == 0) return ChoreGroup.today;
  if (diff <= 7) return ChoreGroup.thisWeek;
  return ChoreGroup.later;
}

/// Tarefas da casa, sincronizadas em tempo real, com o resumo de pontos do mês.
class ChoresView extends StatefulWidget {
  const ChoresView({super.key, required this.household, this.pickPhoto = pickChorePhoto});

  final HouseholdSummary household;

  /// Pede a foto de prova (por omissão, com a câmara ou da galeria).
  final Future<PickedPhoto?> Function(BuildContext context) pickPhoto;

  @override
  State<ChoresView> createState() => _ChoresViewState();
}

class _ChoresViewState extends State<ChoresView> {
  final _subscriptions = <StreamSubscription<Object?>>[];
  Map<String, Chore>? _chores;
  HouseholdDetails? _details;
  List<MemberChoreStats> _stats = const [];
  Object? _error;

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;
  String? get _myUserId => context.read<SessionController>().user?.id;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _subscriptions
      ..add(realtime.events.listen(_onEvent))
      ..add(realtime.reconnected.listen((_) => _load()));
    _load();
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    _reloadStats.dispose();
    super.dispose();
  }

  /// As estatísticas só mudam com as tarefas feitas (PointsChanged), e várias seguidas dão uma só recarga.
  late final _reloadStats = CoalescedReload(_loadStats);

  Future<void> _load() async {
    try {
      final results = await Future.wait([
        _api.getChores(_householdId),
        _api.getHousehold(_householdId),
        _api.getChoreStats(_householdId),
      ]);
      if (!mounted) return;
      setState(() {
        _chores = {for (final c in results[0] as List<Chore>) c.id: c};
        _details = results[1] as HouseholdDetails;
        _stats = results[2] as List<MemberChoreStats>;
        _error = null;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _loadStats() async {
    try {
      final stats = await _api.getChoreStats(_householdId);
      if (mounted) setState(() => _stats = stats);
    } catch (_) {
      // O resumo é secundário; fica o anterior.
    }
  }

  void _onEvent(HouseholdEvent event) {
    switch (event) {
      case ChoreUpserted(:final chore) when chore.householdId == _householdId:
        _upsert(chore);
      case ChoreDeleted(:final choreId):
        setState(() => _chores?.remove(choreId));
      case PointsChanged(:final householdId) when householdId == _householdId:
        _reloadStats.request();
      default:
        break;
    }
  }

  void _upsert(Chore chore, {bool force = false}) {
    final current = _chores?[chore.id];
    if (!force && current != null && current.updatedAt.isAfter(chore.updatedAt)) return;
    setState(() {
      if (chore.isDone) {
        _chores?.remove(chore.id);
      } else {
        (_chores ??= {})[chore.id] = chore;
      }
    });
  }

  ModuleAccess get _access => widget.household.accessTo(HouseholdModule.chores);

  /// Quem gere conclui qualquer tarefa; quem usa, as suas e as de ninguém; quem só vê, nenhuma. Uma tarefa que se
  /// repete só se faz uma vez em cada ocorrência: a seguinte fica à espera do seu dia.
  bool _canComplete(Chore chore) =>
      chore.isAvailableOn(DateTime.now()) &&
      (_access.canManage ||
          (_access.canParticipate && (chore.assigneeUserId == null || chore.assigneeUserId == _myUserId)));

  /// Quem gere marca como feita a tarefa de outra pessoa (a criança sem telemóvel): pergunta quem a fez, com o
  /// responsável escolhido. Os pontos vão para quem a fez.
  Future<String?> _whoDidIt(Chore chore) async {
    final assignee = chore.assigneeUserId;
    final details = _details;
    if (!_access.canManage || assignee == null || assignee == _myUserId || details == null) return _myUserId;
    return showDialog<String>(
      context: context,
      builder: (context) => SimpleDialog(
        title: Text('Quem fez «${chore.title}»?'.tr),
        children: [
          for (final member in details.members)
            SimpleDialogOption(
              onPressed: () => Navigator.pop(context, member.userId),
              child: Row(
                children: [
                  CircleAvatar(
                    radius: 12,
                    backgroundColor: colorFromHex(member.color),
                    foregroundColor: Colors.white,
                    child: Text(
                      member.displayName.isEmpty ? '?' : member.displayName[0].toUpperCase(),
                      style: const TextStyle(fontSize: 11),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(child: Text(member.displayName)),
                  if (member.userId == assignee) const Icon(Icons.check, size: 18),
                ],
              ),
            ),
        ],
      ),
    );
  }

  Future<void> _complete(Chore chore) async {
    try {
      final doneBy = await _whoDidIt(chore);
      if (doneBy == null || !mounted) return;
      String? photoId;
      if (chore.requiresPhoto) {
        final photo = await widget.pickPhoto(context);
        if (photo == null || !mounted) return;
        photoId = await _api.uploadChorePhoto(_householdId, photo.bytes, photo.name);
      }
      final (updated, points) = await _api.completeChore(
        _householdId,
        chore.id,
        photoId: photoId,
        doneByUserId: doneBy == _myUserId ? null : doneBy,
      );
      _upsert(updated);
      _loadStats();
      if (!mounted) return;
      final next = updated.isDone ? null : _details?.member(updated.assigneeUserId)?.displayName;
      final doer = doneBy == _myUserId ? null : _details?.member(doneBy)?.displayName;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              [
                if (doer != null && points > 0)
                  '+$points pontos para $doer!'.tr
                else if (points > 0)
                  '+$points pontos!'.tr
                else
                  'Feito!'.tr,
                if (next != null && updated.rotates) 'Agora é a vez de $next.'.tr,
              ].join(' '),
            ),
          ),
        );
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  /// Marca um passo da rotina; o último conclui-a.
  Future<void> _step(Chore chore, int index, bool done) async {
    // Aparece logo marcado; se o servidor recusar, volta atrás.
    setState(() {
      _chores?[chore.id] = chore.copyWith(
        doneSteps: done
            ? ({...chore.doneSteps, index}.toList()..sort())
            : [...chore.doneSteps.where((i) => i != index)],
      );
    });
    try {
      final result = await _api.setChoreStep(_householdId, chore.id, index, done: done);
      _upsert(result.chore, force: true);
      // Todos os passos feitos, mas a tarefa pede foto: falta a foto para a dar como feita.
      if (!result.completed &&
          result.chore.requiresPhoto &&
          result.chore.doneSteps.length == result.chore.steps.length) {
        await _complete(result.chore);
        return;
      }
      if (!result.completed) return;
      _loadStats();
      if (!mounted) return;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              result.points > 0 ? '${chore.title}: +${result.points} pontos!'.tr : '${chore.title}: feito!'.tr,
            ),
          ),
        );
    } catch (e) {
      setState(() => _chores?[chore.id] = chore);
      if (mounted) showError(context, e);
    }
  }

  Future<void> _edit([Chore? chore]) async {
    final details = _details;
    if (details == null) return;
    final result = await showChoreEditor(context, members: details.members, chore: chore);
    if (result == null || !mounted) return;
    try {
      switch (result) {
        case ChoreSaved(:final draft) when chore == null:
          _upsert(await _api.createChore(_householdId, draft));
        case ChoreSaved(:final draft):
          _upsert(await _api.updateChore(_householdId, chore!.id, draft));
        case ChoreDeleteRequested():
          await _api.deleteChore(_householdId, chore!.id);
          setState(() => _chores?.remove(chore.id));
      }
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final chores = _chores;
    final canManage = _access.canManage;

    if (chores == null) {
      return Center(
        child: _error == null
            ? const CircularProgressIndicator()
            : Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(errorText(_error!)),
                  TextButton(onPressed: _load, child: Text('Tentar de novo'.tr)),
                ],
              ),
      );
    }

    final today = DateTime.now();
    final grouped = <ChoreGroup, List<Chore>>{};
    final sorted = chores.values.toList()
      ..sort((a, b) {
        final byDate = (a.dueDate ?? DateTime(9999)).compareTo(b.dueDate ?? DateTime(9999));
        if (byDate != 0) return byDate;
        // No mesmo dia: primeiro a manhã, depois o resto, por fim a noite.
        final byTime = a.timeOfDay.index.compareTo(b.timeOfDay.index);
        return byTime != 0 ? byTime : a.title.toLowerCase().compareTo(b.title.toLowerCase());
      });
    for (final chore in sorted) {
      grouped.putIfAbsent(choreGroupFor(chore, today), () => []).add(chore);
    }

    return Scaffold(
      floatingActionButton: canManage
          ? FloatingActionButton.extended(onPressed: _edit, icon: const Icon(Icons.add), label: Text('Nova tarefa'.tr))
          : null,
      body: RefreshIndicator(
        onRefresh: _load,
        // As linhas só se constroem quando aparecem no ecrã (P10): numa casa com 300 tarefas, cada alteração já não
        // reconstrói as 300.
        child: _LazyList(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
          rows: [
            if (_stats.any((s) => s.points > 0 || s.completed > 0))
              (context) => _StatsCard(
                stats: _stats,
                onHistory: () =>
                    Navigator.of(context)
                        .push(MaterialPageRoute<void>(builder: (_) => ChoreHistoryScreen(household: widget.household))),
              ),
            if (chores.isEmpty)
              (context) => Padding(
                padding: const EdgeInsets.all(32),
                child: Text(
                  canManage ? 'Sem tarefas por fazer. Cria a primeira!'.tr : 'Sem tarefas por fazer.'.tr,
                  textAlign: TextAlign.center,
                ),
              ),
            for (final group in ChoreGroup.values)
              if (grouped[group] case final items?) ...[
                (context) => Padding(
                  padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
                  child: Text(
                    '${group.label} (${items.length})',
                    style: Theme.of(context).textTheme.titleSmall
                        ?.copyWith(color: group == ChoreGroup.overdue ? Theme.of(context).colorScheme.error : null),
                  ),
                ),
                for (final chore in items)
                  (context) => _ChoreTile(
                    key: ValueKey(chore.id),
                    chore: chore,
                    assignee: _details?.member(chore.assigneeUserId),
                    canComplete: _canComplete(chore),
                    waitsUntil: chore.isAvailableOn(today) ? null : chore.availableFrom,
                    viewOnly: !_access.canParticipate,
                    onComplete: () => _complete(chore),
                    onStep: (index, done) => _step(chore, index, done),
                    onTap: canManage ? () => _edit(chore) : null,
                  ),
              ],
          ],
        ),
      ),
    );
  }
}

/// Uma lista que só constrói as linhas que aparecem no ecrã (P10). Cada linha é uma função que a constrói.
class _LazyList extends StatelessWidget {
  const _LazyList({required this.rows, this.padding});

  final List<WidgetBuilder> rows;
  final EdgeInsetsGeometry? padding;

  @override
  Widget build(BuildContext context) =>
      ListView.builder(padding: padding, itemCount: rows.length, itemBuilder: (context, index) => rows[index](context));
}

class _ChoreTile extends StatelessWidget {
  const _ChoreTile({
    super.key,
    required this.chore,
    required this.assignee,
    required this.canComplete,
    required this.onComplete,
    required this.onStep,
    this.onTap,
    this.viewOnly = false,
    this.waitsUntil,
  });

  final Chore chore;
  final Member? assignee;
  final bool canComplete;

  /// Já está feita por agora: só se volta a poder fazer neste dia.
  final DateTime? waitsUntil;
  final VoidCallback onComplete;
  final void Function(int index, bool done) onStep;
  final VoidCallback? onTap;

  /// Quem só vê as tarefas não as conclui.
  final bool viewOnly;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final localizations = MaterialLocalizations.of(context);
    final details = [
      if (chore.timeOfDay != ChoreTimeOfDay.any) chore.timeOfDay.label,
      if (chore.dueDate != null) localizations.formatShortMonthDay(chore.dueDate!),
      if (chore.recurrence != ChoreRecurrence.none) chore.recurrence.label.toLowerCase(),
      if (chore.rotates) 'em rotação'.tr,
      if (chore.isRoutine) '${chore.doneSteps.length}/${chore.steps.length} passos'.tr,
      if (chore.requiresPhoto) 'com foto'.tr,
      if (waitsUntil != null) 'disponível a ${formatDayMonthShort(waitsUntil!)}'.tr,
    ].join(' · ');

    final tile = ListTile(
      onTap: onTap,
      leading: IconButton(
        tooltip: canComplete
            ? 'Marcar como feita'.tr
            : waitsUntil != null
            ? 'Já está feita. Volta a estar disponível a ${formatDayMonthShort(waitsUntil!)}.'.tr
            : viewOnly
            ? 'Só podes ver as tarefas'.tr
            : 'Atribuída a outra pessoa'.tr,
        onPressed: canComplete ? onComplete : null,
        icon: Icon(switch (chore.timeOfDay) {
          _ when waitsUntil != null => Icons.check_circle_outline,
          ChoreTimeOfDay.morning when chore.isRoutine => Icons.wb_sunny_outlined,
          ChoreTimeOfDay.evening when chore.isRoutine => Icons.bedtime_outlined,
          _ => Icons.radio_button_unchecked,
        }),
        selectedIcon: const Icon(Icons.check_circle),
      ),
      title: Text(chore.title),
      subtitle: details.isEmpty ? null : Text(details),
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (chore.points > 0)
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: Text(
                '${chore.points} pts',
                style: theme.textTheme.labelMedium?.copyWith(color: theme.colorScheme.primary),
              ),
            ),
          Tooltip(
            message: assignee?.displayName ?? 'Qualquer pessoa'.tr,
            child: CircleAvatar(
              radius: 14,
              backgroundColor: assignee == null
                  ? theme.colorScheme.surfaceContainerHighest
                  : colorFromHex(assignee!.color),
              foregroundColor: Colors.white,
              child: assignee == null
                  ? Icon(Icons.groups_outlined, size: 16, color: theme.colorScheme.onSurfaceVariant)
                  : Text(
                      assignee!.displayName.isEmpty ? '?' : assignee!.displayName[0].toUpperCase(),
                      style: const TextStyle(fontSize: 12),
                    ),
            ),
          ),
        ],
      ),
    );

    if (!chore.isRoutine) return Card(child: tile);
    return Card(
      child: Column(
        children: [
          tile,
          for (final (i, step) in chore.steps.indexed)
            CheckboxListTile(
              dense: true,
              contentPadding: const EdgeInsets.only(left: 72, right: 16),
              controlAffinity: ListTileControlAffinity.leading,
              value: chore.doneSteps.contains(i),
              onChanged: canComplete ? (done) => onStep(i, done ?? false) : null,
              title: Text(
                step,
                style: chore.doneSteps.contains(i)
                    ? TextStyle(decoration: TextDecoration.lineThrough, color: theme.colorScheme.outline)
                    : null,
              ),
            ),
          const SizedBox(height: 4),
        ],
      ),
    );
  }
}

class _StatsCard extends StatelessWidget {
  const _StatsCard({required this.stats, required this.onHistory});

  final List<MemberChoreStats> stats;
  final VoidCallback onHistory;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final maxPoints = stats.fold<int>(0, (max, s) => s.points > max ? s.points : max);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(child: Text('Últimos 30 dias'.tr, style: theme.textTheme.titleSmall)),
                TextButton.icon(
                  onPressed: onHistory,
                  icon: const Icon(Icons.history, size: 18),
                  label: Text('Quem fez o quê'.tr),
                ),
              ],
            ),
            const SizedBox(height: 4),
            for (final s in stats)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: Row(
                  children: [
                    SizedBox(width: 96, child: Text(s.displayName, overflow: TextOverflow.ellipsis)),
                    Expanded(
                      child: ClipRRect(
                        borderRadius: BorderRadius.circular(4),
                        child: LinearProgressIndicator(
                          value: maxPoints == 0 ? 0 : s.points / maxPoints,
                          minHeight: 8,
                          color: colorFromHex(s.color),
                          backgroundColor: theme.colorScheme.surfaceContainerHighest,
                        ),
                      ),
                    ),
                    SizedBox(
                      width: 120,
                      child: Text(
                        '${s.points} pts · ${s.completed} ${s.completed == 1 ? 'tarefa'.tr : 'tarefas'.tr}',
                        textAlign: TextAlign.end,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}
