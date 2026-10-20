import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// Histórico da casa (últimos 30 dias): quem mudou o quê, com "Desfazer" nas alterações que se podem desfazer.
class ActivityScreen extends StatefulWidget {
  const ActivityScreen({super.key, required this.household});

  final HouseholdSummary household;

  @override
  State<ActivityScreen> createState() => _ActivityScreenState();
}

class _ActivityScreenState extends State<ActivityScreen> {
  List<ActivityItem>? _items;
  HouseholdDetails? _details;
  Object? _error;
  StreamSubscription<HouseholdEvent>? _events;
  Timer? _reload;

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;

  @override
  void initState() {
    super.initState();
    // Alguém mudou alguma coisa: o histórico volta a ler-se (uma vez, mesmo que cheguem várias).
    _events = context.read<HouseholdRealtime>().events.listen((_) {
      _reload?.cancel();
      _reload = Timer(const Duration(milliseconds: 500), _load);
    });
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    _reload?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final (items, details) = (await _api.getActivity(_householdId), await _api.getHousehold(_householdId));
      if (mounted) {
        setState(() {
          _items = items;
          _details = details;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _undo(ActivityItem item) async {
    try {
      await _api.undoActivity(_householdId, item.id);
      await _load();
      if (!mounted) return;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Desfeito.')));
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  String _name(String? userId) => _details?.member(userId)?.displayName ?? 'Antigo membro';

  @override
  Widget build(BuildContext context) {
    final items = _items;
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Histórico de alterações')),
      body: items == null
          ? Center(
              child: _error == null
                  ? const CircularProgressIndicator()
                  : Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(_error.toString()),
                        TextButton(onPressed: _load, child: const Text('Tentar de novo')),
                      ],
                    ),
            )
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(4, 4, 4, 8),
                    child: Text(
                      'O que mudou nesta casa nos últimos 30 dias. Podes desfazer o que fizeste (e, se gerires a '
                      'área, o que os outros fizeram), começando pelo mais recente.',
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                  if (items.isEmpty)
                    const Padding(
                      padding: EdgeInsets.all(32),
                      child: Text('Ainda não há alterações.', textAlign: TextAlign.center),
                    ),
                  for (final (i, item) in items.indexed) ...[
                    if (i == 0 || !_sameDay(items[i - 1].createdAt, item.createdAt))
                      Padding(
                        padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
                        child: Text(capitalize(formatLongDate(item.createdAt)), style: theme.textTheme.titleSmall),
                      ),
                    _tile(item),
                  ],
                ],
              ),
            ),
    );
  }

  Widget _tile(ActivityItem item) {
    final theme = Theme.of(context);
    final member = _details?.member(item.userId);
    final name = _name(item.userId);
    final undone = item.undoneAt != null;
    return Card(
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: member == null ? null : colorFromHex(member.color),
          foregroundColor: Colors.white,
          child: Icon(_icon(item.module), size: 18),
        ),
        title: Text(
          '$name ${item.summary}',
          style: undone ? TextStyle(decoration: TextDecoration.lineThrough, color: theme.colorScheme.outline) : null,
        ),
        subtitle: Text(
          [
            formatTime(item.createdAt),
            if (item.module != null) item.module!.label,
            if (undone) 'desfeito por ${_name(item.undoneByUserId)}',
          ].join(' · '),
        ),
        trailing: item.canUndo ? TextButton(onPressed: () => _undo(item), child: const Text('Desfazer')) : null,
      ),
    );
  }

  static IconData _icon(HouseholdModule? module) => switch (module) {
    HouseholdModule.shopping => Icons.shopping_cart_outlined,
    HouseholdModule.chores => Icons.task_alt,
    HouseholdModule.calendar => Icons.calendar_month_outlined,
    HouseholdModule.rewards => Icons.card_giftcard_outlined,
    null => Icons.group_outlined,
  };

  static bool _sameDay(DateTime a, DateTime b) => a.year == b.year && a.month == b.month && a.day == b.day;
}
