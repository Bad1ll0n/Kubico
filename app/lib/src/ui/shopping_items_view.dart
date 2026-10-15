import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import 'theme.dart';

/// Artigos de uma lista, sincronizados em tempo real com os outros membros.
class ShoppingItemsView extends StatefulWidget {
  const ShoppingItemsView({super.key, required this.householdId, required this.list, this.showTitle = false});

  final String householdId;
  final ShoppingList list;
  final bool showTitle;

  @override
  State<ShoppingItemsView> createState() => _ShoppingItemsViewState();
}

class _ShoppingItemsViewState extends State<ShoppingItemsView> {
  final _input = TextEditingController();
  final _inputFocus = FocusNode();
  final _subscriptions = <StreamSubscription<Object?>>[];
  Map<String, ShoppingItem>? _items;
  Object? _error;

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
    _input.dispose();
    _inputFocus.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final items = await _api.getItems(widget.householdId, widget.list.id);
      if (mounted) {
        setState(() {
          _items = {for (final i in items) i.id: i};
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  void _onEvent(HouseholdEvent event) {
    switch (event) {
      case ShoppingItemUpserted(:final item) when item.listId == widget.list.id:
        _upsert(item);
      case ShoppingItemDeleted(:final listId, :final itemId) when listId == widget.list.id:
        setState(() => _items?.remove(itemId));
      default:
        break;
    }
  }

  /// Aplica uma versão do artigo, ignorando versões mais antigas que cheguem fora de ordem.
  void _upsert(ShoppingItem item) {
    final current = _items?[item.id];
    if (current != null && current.updatedAt.isAfter(item.updatedAt)) return;
    setState(() => (_items ??= {})[item.id] = item);
  }

  Future<void> _add() async {
    final (name, quantity) = parseItemInput(_input.text);
    if (name.isEmpty) return;
    _input.clear();
    _inputFocus.requestFocus();
    try {
      _upsert(await _api.addItem(widget.householdId, widget.list.id, name: name, quantity: quantity));
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _toggle(ShoppingItem item) async {
    // Otimista: marca já e corrige se o servidor recusar.
    setState(() => _items![item.id] = item.copyWith(isChecked: !item.isChecked));
    try {
      _upsert(await _api.setItemChecked(widget.householdId, widget.list.id, item.id, !item.isChecked));
    } catch (e) {
      if (!mounted) return;
      setState(() => _items![item.id] = item);
      showError(context, e);
    }
  }

  Future<void> _delete(ShoppingItem item) async {
    setState(() => _items!.remove(item.id));
    try {
      await _api.deleteItem(widget.householdId, widget.list.id, item.id);
    } catch (e) {
      if (!mounted) return;
      setState(() => _items![item.id] = item);
      showError(context, e);
    }
  }

  Future<void> _clearChecked() async {
    try {
      await _api.clearChecked(widget.householdId, widget.list.id);
      setState(() => _items!.removeWhere((_, i) => i.isChecked));
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final items = _items;

    if (items == null) {
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

    final pending = items.values.where((i) => !i.isChecked).toList()..sort(_byCategoryThenDate);
    final done = items.values.where((i) => i.isChecked).toList()..sort(_byCategoryThenDate);

    return Column(
      children: [
        if (widget.showTitle)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
            child: Align(
              alignment: Alignment.centerLeft,
              child: Text(widget.list.name, style: theme.textTheme.titleLarge),
            ),
          ),
        Padding(
          padding: const EdgeInsets.all(12),
          child: TextField(
            controller: _input,
            focusNode: _inputFocus,
            textCapitalization: TextCapitalization.sentences,
            decoration: InputDecoration(
              hintText: 'Adicionar artigo (ex.: 2 leite)',
              prefixIcon: const Icon(Icons.add),
              suffixIcon: IconButton(icon: const Icon(Icons.send), tooltip: 'Adicionar', onPressed: _add),
            ),
            onSubmitted: (_) => _add(),
          ),
        ),
        Expanded(
          child: RefreshIndicator(
            onRefresh: _load,
            child: ListView(
              padding: const EdgeInsets.only(bottom: 24),
              children: [
                if (pending.isEmpty && done.isEmpty)
                  Padding(
                    padding: const EdgeInsets.all(32),
                    child: Text('A lista está vazia.', textAlign: TextAlign.center, style: theme.textTheme.bodyLarge),
                  ),
                for (final item in pending) _itemTile(item),
                if (done.isNotEmpty) ...[
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 16, 8, 0),
                    child: Row(
                      children: [
                        Expanded(child: Text('No carrinho (${done.length})', style: theme.textTheme.titleSmall)),
                        TextButton.icon(
                          onPressed: _clearChecked,
                          icon: const Icon(Icons.cleaning_services_outlined, size: 18),
                          label: const Text('Limpar'),
                        ),
                      ],
                    ),
                  ),
                  for (final item in done) _itemTile(item),
                ],
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _itemTile(ShoppingItem item) {
    final theme = Theme.of(context);
    final subtitle = [item.quantity, item.category].whereType<String>().join(' · ');
    return Dismissible(
      key: ValueKey(item.id),
      direction: DismissDirection.endToStart,
      background: Container(
        color: theme.colorScheme.errorContainer,
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.symmetric(horizontal: 24),
        child: Icon(Icons.delete_outline, color: theme.colorScheme.onErrorContainer),
      ),
      onDismissed: (_) => _delete(item),
      child: CheckboxListTile(
        value: item.isChecked,
        onChanged: (_) => _toggle(item),
        controlAffinity: ListTileControlAffinity.leading,
        title: Text(
          item.name,
          style: item.isChecked
              ? TextStyle(decoration: TextDecoration.lineThrough, color: theme.colorScheme.outline)
              : null,
        ),
        subtitle: subtitle.isEmpty ? null : Text(subtitle),
      ),
    );
  }

  static int _byCategoryThenDate(ShoppingItem a, ShoppingItem b) {
    final category = (a.category ?? '￿').toLowerCase().compareTo((b.category ?? '￿').toLowerCase());
    return category != 0 ? category : a.createdAt.compareTo(b.createdAt);
  }
}

/// "2 leite" → ("leite", "2"); "leite" → ("leite", null); "1kg arroz" → ("arroz", "1kg").
(String name, String? quantity) parseItemInput(String raw) {
  final text = raw.trim();
  final match = RegExp(r'^(\d+(?:[.,]\d+)?\s?(?:x|kg|g|l|ml|un)?)\s+(.+)$', caseSensitive: false).firstMatch(text);
  if (match == null) return (text, null);
  return (match.group(2)!.trim(), match.group(1)!.trim());
}
