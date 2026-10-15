import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'chore_editor.dart';
import 'theme.dart';

enum ChoreGroup {
  overdue('Em atraso'),
  today('Hoje'),
  thisWeek('Próximos 7 dias'),
  later('Mais tarde'),
  noDate('Sem prazo');

  const ChoreGroup(this.label);
  final String label;
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
  const ChoresView({super.key, required this.household});

  final HouseholdSummary household;

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
    super.dispose();
  }

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
        _loadStats();
      case ChoreDeleted(:final choreId):
        setState(() => _chores?.remove(choreId));
      default:
        break;
    }
  }

  void _upsert(Chore chore) {
    final current = _chores?[chore.id];
    if (current != null && current.updatedAt.isAfter(chore.updatedAt)) return;
    setState(() {
      if (chore.isDone) {
        _chores?.remove(chore.id);
      } else {
        (_chores ??= {})[chore.id] = chore;
      }
    });
  }

  bool _canComplete(Chore chore) =>
      widget.household.role.canManageContent || chore.assigneeUserId == null || chore.assigneeUserId == _myUserId;

  Future<void> _complete(Chore chore) async {
    try {
      final (updated, points) = await _api.completeChore(_householdId, chore.id);
      _upsert(updated);
      _loadStats();
      if (!mounted) return;
      final next = updated.isDone ? null : _details?.member(updated.assigneeUserId)?.displayName;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              [
                if (points > 0) '+$points pontos!' else 'Feito!',
                if (next != null && updated.rotates) 'Agora é a vez de $next.',
              ].join(' '),
            ),
          ),
        );
    } catch (e) {
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
    final canManage = widget.household.role.canManageContent;

    if (chores == null) {
      return Center(
        child: _error == null
            ? const CircularProgressIndicator()
            : Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(_error.toString()),
                  TextButton(onPressed: _load, child: const Text('Tentar de novo')),
                ],
              ),
      );
    }

    final today = DateTime.now();
    final grouped = <ChoreGroup, List<Chore>>{};
    final sorted = chores.values.toList()
      ..sort((a, b) {
        final byDate = (a.dueDate ?? DateTime(9999)).compareTo(b.dueDate ?? DateTime(9999));
        return byDate != 0 ? byDate : a.title.toLowerCase().compareTo(b.title.toLowerCase());
      });
    for (final chore in sorted) {
      grouped.putIfAbsent(choreGroupFor(chore, today), () => []).add(chore);
    }

    return Scaffold(
      floatingActionButton: canManage
          ? FloatingActionButton.extended(
              onPressed: _edit,
              icon: const Icon(Icons.add),
              label: const Text('Nova tarefa'),
            )
          : null,
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
          children: [
            if (_stats.any((s) => s.points > 0 || s.completed > 0)) _StatsCard(stats: _stats),
            if (chores.isEmpty)
              Padding(
                padding: const EdgeInsets.all(32),
                child: Text(
                  canManage ? 'Sem tarefas por fazer. Cria a primeira!' : 'Sem tarefas por fazer.',
                  textAlign: TextAlign.center,
                ),
              ),
            for (final group in ChoreGroup.values)
              if (grouped[group] case final items?) ...[
                Padding(
                  padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
                  child: Text(
                    '${group.label} (${items.length})',
                    style: Theme.of(context).textTheme.titleSmall
                        ?.copyWith(color: group == ChoreGroup.overdue ? Theme.of(context).colorScheme.error : null),
                  ),
                ),
                for (final chore in items)
                  _ChoreTile(
                    chore: chore,
                    assignee: _details?.member(chore.assigneeUserId),
                    canComplete: _canComplete(chore),
                    onComplete: () => _complete(chore),
                    onTap: canManage ? () => _edit(chore) : null,
                  ),
              ],
          ],
        ),
      ),
    );
  }
}

class _ChoreTile extends StatelessWidget {
  const _ChoreTile({
    required this.chore,
    required this.assignee,
    required this.canComplete,
    required this.onComplete,
    this.onTap,
  });

  final Chore chore;
  final Member? assignee;
  final bool canComplete;
  final VoidCallback onComplete;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final localizations = MaterialLocalizations.of(context);
    final details = [
      if (chore.dueDate != null) localizations.formatShortMonthDay(chore.dueDate!),
      if (chore.recurrence != ChoreRecurrence.none) chore.recurrence.label.toLowerCase(),
      if (chore.rotates) 'em rotação',
    ].join(' · ');

    return Card(
      child: ListTile(
        onTap: onTap,
        leading: IconButton(
          tooltip: canComplete ? 'Marcar como feita' : 'Atribuída a outra pessoa',
          onPressed: canComplete ? onComplete : null,
          icon: const Icon(Icons.radio_button_unchecked),
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
              message: assignee?.displayName ?? 'Qualquer pessoa',
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
      ),
    );
  }
}

class _StatsCard extends StatelessWidget {
  const _StatsCard({required this.stats});

  final List<MemberChoreStats> stats;

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
            Text('Últimos 30 dias', style: theme.textTheme.titleSmall),
            const SizedBox(height: 12),
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
                        '${s.points} pts · ${s.completed} ${s.completed == 1 ? 'tarefa' : 'tarefas'}',
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
