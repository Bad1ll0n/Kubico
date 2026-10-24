import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/household_realtime.dart';
import '../shopping/shopping_repository.dart';
import '../shopping/voice_input.dart';
import 'scanner_screen.dart';
import 'shopping_add_dialogs.dart';
import 'theme.dart';

/// Artigos de uma lista, sincronizados em tempo real com os outros membros. Funciona sem rede: as alterações
/// aparecem logo e são enviadas quando a ligação voltar (ver [ShoppingRepository]).
class ShoppingItemsView extends StatefulWidget {
  const ShoppingItemsView({
    super.key,
    required this.householdId,
    required this.list,
    this.showTitle = false,
    this.canEdit = true,
    this.voice,
    this.scanBarcode,
  });

  final String householdId;
  final ShoppingList list;
  final bool showTitle;

  /// Falso para quem só vê as compras: sem adicionar, marcar nem apagar artigos.
  final bool canEdit;

  /// O ditado (por omissão, o do sistema).
  final VoiceInput? voice;

  /// Lê um código de barras (por omissão, com a câmara; só onde houver câmara).
  final Future<String?> Function(BuildContext context)? scanBarcode;

  @override
  State<ShoppingItemsView> createState() => _ShoppingItemsViewState();
}

class _ShoppingItemsViewState extends State<ShoppingItemsView> {
  final _input = TextEditingController();
  final _inputFocus = FocusNode();
  final _subscriptions = <StreamSubscription<Object?>>[];
  Map<String, ShoppingItem>? _items;
  Object? _error;

  /// Os artigos por comprar e no carrinho, já ordenados; voltam a ordenar-se só quando os artigos mudam (P10).
  (List<ShoppingItem>, List<ShoppingItem>)? _sorted;

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
          _sorted = null;
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
        setState(() {
          _items?.remove(itemId);
          _sorted = null;
        });
      case ShoppingItemsDeleted(:final listId, :final itemIds) when listId == widget.list.id:
        final gone = itemIds.toSet();
        setState(() {
          _items?.removeWhere((id, _) => gone.contains(id));
          _sorted = null;
        });
      default:
        break;
    }
  }

  /// Aplica uma versão do artigo, ignorando versões mais antigas que cheguem fora de ordem.
  void _upsert(ShoppingItem item) {
    final current = _items?[item.id];
    if (current != null && current.updatedAt.isAfter(item.updatedAt)) return;
    setState(() {
      (_items ??= {})[item.id] = item;
      _sorted = null;
    });
  }

  // As alterações aparecem logo; o repositório envia-as (já, ou quando a rede voltar).

  void _add() {
    final (name, quantity) = parseItemInput(_input.text);
    if (name.isEmpty) return;
    _input.clear();
    _inputFocus.requestFocus();
    _upsert(_shopping.addItem(_householdId, _listId, name: name, quantity: quantity));
  }

  static Future<String?> _scanWithCamera(BuildContext context) => Navigator.of(context).push<String>(
    MaterialPageRoute(
      builder: (_) => ScannerScreen<String>(
        title: 'Ler código de barras'.tr,
        hint: 'Aponta para o código de barras do produto'.tr,
        accept: (raw) => isValidBarcode(raw.trim()) ? raw.trim() : null,
      ),
    ),
  );

  void _tell(String message) => ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message)));

  /// Lê o código, procura o produto e confirma o nome antes de o pôr na lista (a casa fica com esse nome).
  Future<void> _scan() async {
    final barcode = await (widget.scanBarcode ?? _scanWithCamera)(context);
    if (barcode == null || !mounted) return;
    final already = _items?.values.where((i) => i.barcode == barcode && !i.isChecked).firstOrNull;
    if (already != null) {
      _tell('«${already.name}» já está na lista.'.tr);
      return;
    }

    Product? product;
    String? problem;
    try {
      product = await _shopping.api.lookupProduct(_householdId, barcode);
      if (product == null) {
        problem = 'Não conhecemos este produto. Escreve o nome: fica guardado para a próxima vez.'.tr;
      }
    } on ApiException catch (e) {
      problem = e.message;
    } catch (_) {
      problem = 'Sem ligação: escreve o nome do produto.'.tr;
    }
    if (!mounted) return;

    final item = await showDialog<ScannedItem>(
      context: context,
      builder: (_) => ScannedProductDialog(barcode: barcode, product: product, problem: problem),
    );
    if (item == null) return;
    _upsert(_shopping.addItem(_householdId, _listId, name: item.name, category: item.category, barcode: barcode));
  }

  /// Ouve os artigos ditos e põe-nos todos na lista.
  Future<void> _dictate() async {
    final text = await showDialog<String>(
      context: context,
      builder: (_) => DictationDialog(voice: widget.voice ?? SpeechVoiceInput.shared),
    );
    if (text == null || !mounted) return;
    final added = <String>[];
    for (final entry in parseSpokenItems(text)) {
      final (name, quantity) = parseItemInput(entry);
      if (name.isEmpty) continue;
      _upsert(_shopping.addItem(_householdId, _listId, name: name, quantity: quantity));
      added.add(name);
    }
    if (added.isNotEmpty) {
      _tell(added.length == 1 ? 'Adicionado: ${added.single}.'.tr : 'Adicionados ${added.length} artigos.'.tr);
    }
  }

  void _toggle(ShoppingItem item) {
    setState(() {
      _items![item.id] = item.copyWith(isChecked: !item.isChecked);
      _sorted = null;
    });
    _shopping.setChecked(_householdId, _listId, item.id, !item.isChecked);
  }

  void _delete(ShoppingItem item) {
    setState(() {
      _items!.remove(item.id);
      _sorted = null;
    });
    _shopping.deleteItem(_householdId, _listId, item.id);
  }

  void _clearChecked() {
    setState(() {
      _items!.removeWhere((_, i) => i.isChecked);
      _sorted = null;
    });
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
                  Text(errorText(_error!)),
                  TextButton(onPressed: _load, child: Text('Tentar de novo'.tr)),
                ],
              ),
      );
    }

    final (pending, done) = _sorted ??= (
      items.values.where((i) => !i.isChecked).toList()..sort(_byCategoryThenDate),
      items.values.where((i) => i.isChecked).toList()..sort(_byCategoryThenDate),
    );

    // Uma lista plana de linhas (artigos por comprar, o cabeçalho do carrinho e os artigos comprados), construída só
    // à medida que aparecem no ecrã: o custo de um toque deixa de depender do tamanho da lista (P10).
    final empty = pending.isEmpty && done.isEmpty;
    final rowCount = empty ? 1 : pending.length + (done.isEmpty ? 0 : 1 + done.length);

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
        if (widget.canEdit)
          Padding(
            padding: const EdgeInsets.all(12),
            child: TextField(
              controller: _input,
              focusNode: _inputFocus,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(
                hintText: 'Adicionar artigo (ex.: 2 leite)'.tr,
                prefixIcon: const Icon(Icons.add),
                suffixIcon: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (widget.scanBarcode != null || scannerAvailable)
                      IconButton(
                        icon: const Icon(Icons.qr_code_scanner),
                        tooltip: 'Ler código de barras'.tr,
                        onPressed: _scan,
                      ),
                    IconButton(icon: const Icon(Icons.mic_none), tooltip: 'Ditar artigos'.tr, onPressed: _dictate),
                    IconButton(icon: const Icon(Icons.send), tooltip: 'Adicionar'.tr, onPressed: _add),
                  ],
                ),
              ),
              onSubmitted: (_) => _add(),
            ),
          ),
        Expanded(
          child: RefreshIndicator(
            onRefresh: _load,
            child: ListView.builder(
              padding: const EdgeInsets.only(bottom: 24),
              itemCount: rowCount,
              itemBuilder: (context, index) {
                if (empty) {
                  return Padding(
                    padding: const EdgeInsets.all(32),
                    child: Text(
                      'A lista está vazia.'.tr,
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyLarge,
                    ),
                  );
                }
                if (index < pending.length) return _itemTile(pending[index]);
                if (index > pending.length) return _itemTile(done[index - pending.length - 1]);
                return Padding(
                  key: const ValueKey('carrinho'),
                  padding: const EdgeInsets.fromLTRB(16, 16, 8, 0),
                  child: Row(
                    children: [
                      Expanded(child: Text('No carrinho (${done.length})'.tr, style: theme.textTheme.titleSmall)),
                      if (widget.canEdit)
                        TextButton.icon(
                          onPressed: _clearChecked,
                          icon: const Icon(Icons.cleaning_services_outlined, size: 18),
                          label: Text('Limpar'.tr),
                        ),
                    ],
                  ),
                );
              },
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
      direction: widget.canEdit ? DismissDirection.endToStart : DismissDirection.none,
      background: Container(
        color: theme.colorScheme.errorContainer,
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.symmetric(horizontal: 24),
        child: Icon(Icons.delete_outline, color: theme.colorScheme.onErrorContainer),
      ),
      onDismissed: (_) => _delete(item),
      child: CheckboxListTile(
        value: item.isChecked,
        onChanged: widget.canEdit ? (_) => _toggle(item) : null,
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
      1 => ' 1 alteração por enviar.'.tr,
      _ => ' $pending alterações por enviar.'.tr,
    };
    return Material(
      color: theme.colorScheme.secondaryContainer,
      child: ListTile(
        dense: true,
        leading: Icon(offline ? Icons.cloud_off : Icons.cloud_upload_outlined),
        title: Text(offline ? 'Sem ligação: podes continuar a usar a lista.$waiting'.tr : 'A enviar as alterações…'.tr),
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
