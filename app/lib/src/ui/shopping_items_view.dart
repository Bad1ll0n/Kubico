import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/models.dart';
import '../realtime/household_realtime.dart';
import '../shopping/shopping_repository.dart';

/// Artigos de uma lista, sincronizados em tempo real com os outros membros. Funciona sem rede: as alterações
/// aparecem logo e são enviadas quando a ligação voltar (ver [ShoppingRepository]).
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

  late final ShoppingRepository _shopping;
  String get _householdId => widget.householdId;
  String get _listId => widget.list.id;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _shopping = context.read<ShoppingRepository>();
    _subscriptions
      ..add(realtime.events.listen(_onEvent))
      ..add(realtime.reconnected.listen((_) => _load()));
    // A fila de alterações enviou-se (ou a rede voltou): o servidor tem a versão final.
    _shopping.addListener(_onShoppingChanged);
    _load();
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    _shopping.removeListener(_onShoppingChanged);
    _input.dispose();
    _inputFocus.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final items = await _shopping.items(_householdId, _listId);
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

  /// Havia alterações à espera por falta de rede: quando todas chegarem, relê a lista do servidor.
  var _waitedOffline = false;

  void _onShoppingChanged() {
    final waiting = _shopping.pendingCount > 0;
    if (_waitedOffline && !waiting) _load();
    _waitedOffline = waiting && (_waitedOffline || _shopping.isOffline);
    if (mounted) setState(() {});
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

  // As alterações aparecem logo; o repositório envia-as (já, ou quando a rede voltar).

  void _add() {
    final (name, quantity) = parseItemInput(_input.text);
    if (name.isEmpty) return;
    _input.clear();
    _inputFocus.requestFocus();
    _upsert(_shopping.addItem(_householdId, _listId, name: name, quantity: quantity));
  }

  void _toggle(ShoppingItem item) {
    setState(() => _items![item.id] = item.copyWith(isChecked: !item.isChecked));
    _shopping.setChecked(_householdId, _listId, item.id, !item.isChecked);
  }

  void _delete(ShoppingItem item) {
    setState(() => _items!.remove(item.id));
    _shopping.deleteItem(_householdId, _listId, item.id);
  }

  void _clearChecked() {
    setState(() => _items!.removeWhere((_, i) => i.isChecked));
    _shopping.clearChecked(_householdId, _listId);
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
        if (_shopping.isOffline || _shopping.pendingCount > 0)
          _SyncBanner(offline: _shopping.isOffline, pending: _shopping.pendingCount),
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

/// Avisa que não há rede (e quantas alterações esperam) ou que as alterações estão a ser enviadas.
class _SyncBanner extends StatelessWidget {
  const _SyncBanner({required this.offline, required this.pending});

  final bool offline;
  final int pending;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final waiting = switch (pending) {
      0 => '',
      1 => ' 1 alteração por enviar.',
      _ => ' $pending alterações por enviar.',
    };
    return Material(
      color: theme.colorScheme.secondaryContainer,
      child: ListTile(
        dense: true,
        leading: Icon(offline ? Icons.cloud_off : Icons.cloud_upload_outlined),
        title: Text(offline ? 'Sem ligação: podes continuar a usar a lista.$waiting' : 'A enviar as alterações…'),
      ),
    );
  }
}

/// "2 leite" → ("leite", "2"); "leite" → ("leite", null); "1kg arroz" → ("arroz", "1kg").
(String name, String? quantity) parseItemInput(String raw) {
  final text = raw.trim();
  final match = RegExp(r'^(\d+(?:[.,]\d+)?\s?(?:x|kg|g|l|ml|un)?)\s+(.+)$', caseSensitive: false).firstMatch(text);
  if (match == null) return (text, null);
  return (match.group(2)!.trim(), match.group(1)!.trim());
}
