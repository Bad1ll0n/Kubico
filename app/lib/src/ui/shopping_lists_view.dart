import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import '../shopping/shopping_repository.dart';
import 'theme.dart';

class ShoppingListsView extends StatefulWidget {
  const ShoppingListsView({
    super.key,
    required this.household,
    required this.onOpen,
    required this.onDeleted,
    this.selectedListId,
  });

  final HouseholdSummary household;
  final String? selectedListId;
  final ValueChanged<ShoppingList> onOpen;
  final ValueChanged<String> onDeleted;

  @override
  State<ShoppingListsView> createState() => _ShoppingListsViewState();
}

class _ShoppingListsViewState extends State<ShoppingListsView> {
  List<ShoppingList>? _lists;
  Object? _error;
  final _subscriptions = <StreamSubscription<Object?>>[];

  ApiClient get _api => context.read<ApiClient>();

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
      final lists = await context.read<ShoppingRepository>().lists(widget.household.id);
      if (mounted) {
        setState(() {
          _lists = lists;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  void _onEvent(HouseholdEvent event) {
    switch (event) {
      case ShoppingListUpserted(:final list) when list.householdId == widget.household.id:
        setState(() {
          _lists = [..._lists ?? <ShoppingList>[]].where((l) => l.id != list.id).toList()
            ..add(list)
            ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
        });
      case ShoppingListDeleted(:final listId):
        setState(() => _lists = _lists?.where((l) => l.id != listId).toList());
        widget.onDeleted(listId);
      case ShoppingItemUpserted() || ShoppingItemDeleted():
        // As contagens mudaram; recarregar é barato e evita contas erradas.
        _load();
      default:
        break;
    }
  }

  Future<void> _createList() async {
    final name = await _askName(context);
    if (name == null || name.isEmpty) return;
    try {
      final list = await _api.createList(widget.household.id, name);
      _onEvent(ShoppingListUpserted(list));
      widget.onOpen(list);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _deleteList(ShoppingList list) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Apagar "${list.name}"?'),
        content: const Text('Os artigos desta lista também são apagados para toda a casa.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancelar')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Apagar')),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await _api.deleteList(widget.household.id, list.id);
      _onEvent(ShoppingListDeleted(list.id));
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final canManage = widget.household.accessTo(HouseholdModule.shopping).canManage;
    final lists = _lists;

    Widget body;
    if (lists == null && _error != null) {
      body = _Message(
        icon: Icons.cloud_off,
        text: _error.toString(),
        action: TextButton(onPressed: _load, child: const Text('Tentar de novo')),
      );
    } else if (lists == null) {
      body = const Center(child: CircularProgressIndicator());
    } else if (lists.isEmpty) {
      body = _Message(
        icon: Icons.checklist_rtl,
        text: canManage ? 'Ainda não há listas. Cria a primeira!' : 'Ainda não há listas nesta casa.',
      );
    } else {
      body = RefreshIndicator(
        onRefresh: _load,
        child: ListView.builder(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
          itemCount: lists.length,
          itemBuilder: (context, index) {
            final list = lists[index];
            return Card(
              child: ListTile(
                selected: list.id == widget.selectedListId,
                leading: const Icon(Icons.shopping_cart_outlined),
                title: Text(list.name),
                subtitle: Text(list.pendingCount == 0 ? 'Tudo comprado' : '${list.pendingCount} por comprar'),
                trailing: canManage
                    ? IconButton(
                        tooltip: 'Apagar lista',
                        icon: const Icon(Icons.delete_outline),
                        onPressed: () => _deleteList(list),
                      )
                    : null,
                onTap: () => widget.onOpen(list),
              ),
            );
          },
        ),
      );
    }

    return Scaffold(
      body: body,
      floatingActionButton: canManage
          ? FloatingActionButton.extended(
              onPressed: _createList,
              icon: const Icon(Icons.add),
              label: const Text('Nova lista'),
            )
          : null,
    );
  }
}

Future<String?> _askName(BuildContext context) {
  final controller = TextEditingController();
  return showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Nova lista'),
      content: TextField(
        controller: controller,
        autofocus: true,
        decoration: const InputDecoration(hintText: 'Ex.: Supermercado'),
        textCapitalization: TextCapitalization.sentences,
        onSubmitted: (value) => Navigator.pop(context, value.trim()),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(onPressed: () => Navigator.pop(context, controller.text.trim()), child: const Text('Criar')),
      ],
    ),
  ).whenComplete(controller.dispose);
}

class _Message extends StatelessWidget {
  const _Message({required this.icon, required this.text, this.action});

  final IconData icon;
  final String text;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 48, color: theme.colorScheme.outline),
            const SizedBox(height: 12),
            Text(text, textAlign: TextAlign.center),
            if (action != null) ...[const SizedBox(height: 8), action!],
          ],
        ),
      ),
    );
  }
}
