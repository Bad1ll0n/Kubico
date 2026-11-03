import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show CustomSemanticsAction;
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/household_realtime.dart';
import '../shopping/shopping_order.dart';
import '../shopping/shopping_repository.dart';
import '../shopping/voice_input.dart';
import 'design/components.dart';
import 'first_steps.dart';
import 'scanner_support.dart';
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

  /// "Costumas comprar" e a ordem do supermercado aprendida (F4-22); null enquanto não chegam (ou sem rede).
  ShoppingInsights? _insights;

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
      ..add(realtime.reconnected.listen((_) => _load()))
      ..add(_shopping.rejections.listen(_onRejected));
    // A fila de alterações enviou-se (ou a rede voltou): o servidor tem a versão final.
    _shopping.addListener(_onShoppingChanged);
    ShoppingOrder.learned.addListener(_resort);
    _load();
  }

  void _resort() => setState(() => _sorted = null);

  /// As sugestões e a ordem aprendida: um extra, que nunca impede a lista de abrir.
  Future<void> _loadInsights() async {
    try {
      final insights = await context.read<ApiClient>().getShoppingInsights(_householdId, _listId);
      if (!mounted) return;
      setState(() {
        _insights = insights;
        _sorted = null;
      });
    } catch (_) {
      // Sem rede ou sem acesso: a lista funciona sem elas.
    }
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    _shopping.removeListener(_onShoppingChanged);
    ShoppingOrder.learned.removeListener(_resort);
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
        unawaited(_loadInsights());
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

  /// O servidor recusou uma alteração desta lista: avisa e volta a mostrar o que lá está de facto.
  void _onRejected(ShoppingRejection rejection) {
    if (rejection.change.listId != _listId || !mounted) return;
    _tell('Não foi possível guardar uma alteração à lista: ${rejection.message}'.tr);
    _load();
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

  static Future<String?> _scanWithCamera(BuildContext context) => scanCode(
    context,
    title: 'Ler código de barras'.tr,
    hint: 'Aponta para o código de barras do produto'.tr,
    accept: (raw) => isValidBarcode(raw.trim()) ? raw.trim() : null,
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
    HapticFeedback.selectionClick();
    _shopping.setChecked(_householdId, _listId, item.id, !item.isChecked);
  }

  void _delete(ShoppingItem item) {
    setState(() {
      _items!.remove(item.id);
      _sorted = null;
    });
    _shopping.deleteItem(_householdId, _listId, item.id);
    // Apagar com um deslize engana-se facilmente (sobretudo num tablet partilhado): dá para voltar atrás logo.
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text('«${item.name}» saiu da lista.'.tr),
          action: SnackBarAction(
            label: 'Desfazer'.tr,
            onPressed: () => _upsert(
              _shopping.addItem(
                _householdId,
                _listId,
                name: item.name,
                quantity: item.quantity,
                category: item.category,
                barcode: item.barcode,
              ),
            ),
          ),
        ),
      );
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
      return _error == null
          ? const Center(child: CircularProgressIndicator())
          : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load);
    }

    final (pending, done) = _sorted ??= (
      items.values.where((i) => !i.isChecked).toList()..sort(_compare),
      items.values.where((i) => i.isChecked).toList()..sort(_compare),
    );

    // Uma lista plana de linhas (artigos por comprar, o cabeçalho do carrinho e os artigos comprados), construída só
    // à medida que aparecem no ecrã: o custo de um toque deixa de depender do tamanho da lista (P10).
    final empty = pending.isEmpty && done.isEmpty;
    final pendingRows = _pendingRows(pending);
    final rowCount = empty ? 1 : pendingRows.length + (done.isEmpty ? 0 : 1 + done.length);

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
        // Quanto já está no carrinho ("6 de 14").
        if (!empty)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
            child: Row(
              children: [
                Expanded(
                  child: LinearProgressIndicator(
                    value: done.length / (pending.length + done.length),
                    minHeight: 6,
                    borderRadius: BorderRadius.circular(3),
                    color: context.kubico.area(KubicoArea.shopping).color,
                  ),
                ),
                const SizedBox(width: 12),
                Text(
                  '${done.length} de ${pending.length + done.length}'.tr,
                  style: theme.textTheme.labelMedium?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                    fontFeatures: tabularFigures,
                  ),
                ),
              ],
            ),
          ),
        if (widget.canEdit)
          FirstTimeTip(
            id: 'shopping',
            text: 'Desliza um artigo para a direita para o marcar, e para a esquerda para o apagar.'.tr,
          ),
        _ListTools(
          learnedOrder: ShoppingOrder.learned.value,
          hasLearnedOrder: _insights?.categoryOrder.isNotEmpty ?? false,
          onLearnedOrder: ShoppingOrder.set,
          onRecurring: _showRecurring,
        ),
        if (widget.canEdit && (_insights?.suggestions.isNotEmpty ?? false)) _suggestions(_insights!.suggestions),
        if (widget.canEdit)
          Padding(
            padding: const EdgeInsets.all(12),
            child: TextField(
              controller: _input,
              focusNode: _inputFocus,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(
                hintText: 'Adicionar artigo (ex.: 2 leite)'.tr,
                prefixIcon: const Icon(Icons.add_rounded),
                suffixIcon: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (widget.scanBarcode != null || scannerAvailable)
                      IconButton(
                        icon: const Icon(Icons.qr_code_scanner_rounded),
                        tooltip: 'Ler código de barras'.tr,
                        onPressed: _scan,
                      ),
                    IconButton(
                      icon: const Icon(Icons.mic_none_rounded),
                      tooltip: 'Ditar artigos'.tr,
                      onPressed: _dictate,
                    ),
                    IconButton(icon: const Icon(Icons.add_rounded), tooltip: 'Adicionar'.tr, onPressed: _add),
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
                  return StateView(
                    inList: true,
                    icon: Icons.shopping_basket_rounded,
                    illustration: KubicoIllustration.basket,
                    color: context.kubico.area(KubicoArea.shopping).color,
                    title: 'A lista está vazia'.tr,
                    message: 'Escreve, dita ou lê o código de barras do que falta.'.tr,
                  );
                }
                if (index < pendingRows.length) {
                  return switch (pendingRows[index]) {
                    final ShoppingItem item => _itemTile(item),
                    final (String, int) header => _categoryHeader(header.$1, header.$2),
                    _ => const SizedBox.shrink(),
                  };
                }
                if (index > pendingRows.length) return _itemTile(done[index - pendingRows.length - 1]);
                return Padding(
                  key: const ValueKey('carrinho'),
                  padding: const EdgeInsets.fromLTRB(16, 16, 8, 0),
                  child: Row(
                    children: [
                      Expanded(child: Text('No carrinho (${done.length})'.tr, style: theme.textTheme.titleSmall)),
                      if (widget.canEdit)
                        TextButton.icon(
                          onPressed: _clearChecked,
                          icon: const Icon(Icons.remove_shopping_cart_rounded, size: 18),
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

  /// Os artigos por comprar com um cabeçalho por categoria ("Lacticínios · 2"), quando a lista tem categorias (já
  /// estão ordenados por categoria). Guarda-se para a mesma lista ordenada: não se refaz a cada desenho.
  List<Object> _pendingRows(List<ShoppingItem> pending) {
    if (identical(pending, _rowsFor)) return _rows;
    _rowsFor = pending;
    if (pending.every((i) => i.category == null)) return _rows = pending;
    final counts = <String?, int>{};
    for (final i in pending) {
      counts[i.category] = (counts[i.category] ?? 0) + 1;
    }
    final rows = <Object>[];
    String? current;
    var first = true;
    for (final item in pending) {
      if (first || item.category != current) {
        rows.add((item.category ?? 'Outros'.tr, counts[item.category]!));
        current = item.category;
        first = false;
      }
      rows.add(item);
    }
    return _rows = rows;
  }

  List<ShoppingItem>? _rowsFor;
  List<Object> _rows = const [];

  Widget _categoryHeader(String category, int count) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 2),
      child: Text(
        '${category.toUpperCase()} · $count',
        style: theme.textTheme.labelMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant, letterSpacing: 0.6),
      ),
    );
  }

  Widget _itemTile(ShoppingItem item) {
    final theme = Theme.of(context);
    // A categoria já está no cabeçalho (quando há cabeçalhos); a quantidade fica.
    final subtitle = [item.quantity, if (item.isChecked) item.category].whereType<String>().join(' · ');
    final animate = !MediaQuery.disableAnimationsOf(context);
    // Gestos (F4-04): para a direita marca (ou desmarca), para a esquerda apaga. O leitor de ecrã tem as mesmas ações.
    return Dismissible(
      key: ValueKey(item.id),
      direction: widget.canEdit ? DismissDirection.horizontal : DismissDirection.none,
      background: Container(
        color: context.kubico.successContainer,
        alignment: Alignment.centerLeft,
        padding: const EdgeInsets.symmetric(horizontal: 24),
        child: Icon(
          item.isChecked ? Icons.undo_rounded : Icons.check_rounded,
          color: context.kubico.onSuccessContainer,
        ),
      ),
      secondaryBackground: Container(
        color: theme.colorScheme.errorContainer,
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.symmetric(horizontal: 24),
        child: Icon(Icons.delete_outline_rounded, color: theme.colorScheme.onErrorContainer),
      ),
      confirmDismiss: (direction) async {
        if (direction == DismissDirection.endToStart) return true;
        _toggle(item);
        return false;
      },
      onDismissed: (_) => _delete(item),
      child: Semantics(
        customSemanticsActions: widget.canEdit
            ? {CustomSemanticsAction(label: 'Apagar «${item.name}»'.tr): () => _delete(item)}
            : const {},
        child: CheckboxListTile(
          value: item.isChecked,
          onChanged: widget.canEdit ? (_) => _toggle(item) : null,
          secondary: widget.canEdit
              ? IconButton(
                  tooltip: 'Repetir «${item.name}»'.tr,
                  icon: const Icon(Icons.repeat_rounded),
                  onPressed: () => _repeat(item),
                )
              : null,
          controlAffinity: ListTileControlAffinity.leading,
          // Marcar risca o nome com uma pequena animação (F4-05), sem ela com menos movimento pedido.
          title: AnimatedDefaultTextStyle(
            duration: animate ? const Duration(milliseconds: 250) : Duration.zero,
            style: (theme.textTheme.bodyLarge ?? const TextStyle()).copyWith(
              decoration: item.isChecked ? TextDecoration.lineThrough : TextDecoration.none,
              color: item.isChecked ? theme.colorScheme.onSurfaceVariant : theme.colorScheme.onSurface,
            ),
            child: Text(item.name),
          ),
          subtitle: subtitle.isEmpty ? null : Text(subtitle),
        ),
      ),
    );
  }

  /// Por categoria (na ordem aprendida, se houver e estiver ligada; senão alfabética, sem categoria no fim) e depois
  /// pela ordem em que se puseram.
  int _compare(ShoppingItem a, ShoppingItem b) {
    final learned = ShoppingOrder.learned.value ? _insights?.categoryOrder : null;
    int rank(ShoppingItem i) {
      final category = i.category?.toLowerCase();
      if (category == null) return 1 << 20;
      final index = learned?.indexWhere((c) => c.toLowerCase() == category) ?? -1;
      return index < 0 ? 1 << 19 : index;
    }

    final byRank = rank(a).compareTo(rank(b));
    if (byRank != 0) return byRank;
    final category = (a.category ?? '￿').toLowerCase().compareTo((b.category ?? '￿').toLowerCase());
    return category != 0 ? category : a.createdAt.compareTo(b.createdAt);
  }

  /// "Costumas comprar": um toque põe o artigo na lista.
  Widget _suggestions(List<PurchaseSuggestion> suggestions) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Costumas comprar'.tr, style: theme.textTheme.labelLarge),
          const SizedBox(height: 4),
          Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              for (final s in suggestions)
                ActionChip(
                  avatar: const Icon(Icons.add_rounded, size: 18),
                  label: Text(s.name),
                  tooltip: 'Costumas comprar a cada ${s.everyDays} dias'.tr,
                  onPressed: () {
                    HapticFeedback.selectionClick();
                    _upsert(_shopping.addItem(_householdId, _listId, name: s.name, category: s.category));
                    setState(() {
                      _insights = ShoppingInsights(
                        suggestions: [..._insights!.suggestions.where((x) => x != s)],
                        categoryOrder: _insights!.categoryOrder,
                      );
                    });
                  },
                ),
            ],
          ),
        ],
      ),
    );
  }

  /// "Repetir": o artigo volta à lista sozinho (F4-22a).
  Future<void> _repeat(ShoppingItem item) async {
    final days = await showDialog<int>(
      context: context,
      builder: (context) => SimpleDialog(
        title: Text('Repetir «${item.name}»'.tr),
        children: [
          for (final (days, label) in [
            (7, 'Todas as semanas'.tr),
            (14, 'A cada duas semanas'.tr),
            (30, 'Todos os meses'.tr),
            (1, 'Todos os dias'.tr),
          ])
            SimpleDialogOption(onPressed: () => Navigator.pop(context, days), child: Text(label)),
        ],
      ),
    );
    if (days == null || !mounted) return;
    try {
      await context.read<ApiClient>().createRecurringItem(
        _householdId,
        _listId,
        name: item.name,
        intervalDays: days,
        quantity: item.quantity,
        category: item.category,
      );
      _tell('«${item.name}» volta à lista sozinho.'.tr);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  /// Os artigos que se repetem nesta lista, para os ver e deixar de repetir.
  Future<void> _showRecurring() async {
    final api = context.read<ApiClient>();
    List<RecurringItem> items;
    try {
      items = await api.getRecurringItems(_householdId, _listId);
    } catch (e) {
      if (mounted) showError(context, e);
      return;
    }
    if (!mounted) return;
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      builder: (context) => StatefulBuilder(
        builder: (context, setSheetState) => ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.only(bottom: 24),
          children: [
            ListTile(title: Text('Artigos que se repetem'.tr, style: Theme.of(context).textTheme.titleMedium)),
            if (items.isEmpty)
              Padding(
                padding: const EdgeInsets.all(16),
                child: Text('Nenhum. Mantém um artigo carregado e escolhe "Repetir".'.tr),
              ),
            for (final r in items)
              ListTile(
                leading: const Icon(Icons.repeat_rounded),
                title: Text(r.name),
                subtitle: Text('A cada ${r.intervalDays} dias · próxima a ${r.nextDate.day}/${r.nextDate.month}'.tr),
                trailing: widget.canEdit
                    ? IconButton(
                        tooltip: 'Deixar de repetir'.tr,
                        icon: const Icon(Icons.delete_outline_rounded),
                        onPressed: () async {
                          try {
                            await api.deleteRecurringItem(_householdId, _listId, r.id);
                            setSheetState(() => items = [...items.where((x) => x.id != r.id)]);
                          } catch (e) {
                            if (context.mounted) showError(context, e);
                          }
                        },
                      )
                    : null,
              ),
          ],
        ),
      ),
    );
  }
}

/// Avisa que não há rede (e quantas alterações esperam) ou que as alterações estão a ser enviadas.
class _SyncBanner extends StatelessWidget {
  const _SyncBanner({required this.offline, required this.pending});

  final bool offline;
  final int pending;

  @override
  Widget build(BuildContext context) {
    final waiting = switch (pending) {
      0 => '',
      1 => ' 1 alteração por enviar.'.tr,
      _ => ' $pending alterações por enviar.'.tr,
    };
    // Sem ligação é um estado que se tem de perceber (as alterações ficam à espera): cor de aviso; a enviar, informação.
    final colors = context.kubico;
    final (background, foreground) = offline
        ? (colors.warningContainer, colors.onWarningContainer)
        : (colors.infoContainer, colors.onInfoContainer);
    return Material(
      color: background,
      child: ListTile(
        dense: true,
        iconColor: foreground,
        textColor: foreground,
        leading: Icon(offline ? Icons.cloud_off_rounded : Icons.cloud_upload_rounded),
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

/// A linha de ferramentas da lista: a ordem do supermercado e os artigos que se repetem.
class _ListTools extends StatelessWidget {
  const _ListTools({
    required this.learnedOrder,
    required this.hasLearnedOrder,
    required this.onLearnedOrder,
    required this.onRecurring,
  });

  final bool learnedOrder;
  final bool hasLearnedOrder;
  final ValueChanged<bool> onLearnedOrder;
  final VoidCallback onRecurring;

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: Alignment.centerRight,
      child: PopupMenuButton<String>(
        tooltip: 'Mais opções da lista'.tr,
        icon: const Icon(Icons.tune_rounded),
        onSelected: (value) => switch (value) {
          'order' => onLearnedOrder(!learnedOrder),
          _ => onRecurring(),
        },
        itemBuilder: (_) => [
          CheckedPopupMenuItem(
            value: 'order',
            checked: learnedOrder,
            enabled: hasLearnedOrder || learnedOrder,
            child: Text('Pela ordem em que costumo comprar'.tr),
          ),
          PopupMenuItem(value: 'recurring', child: Text('Artigos que se repetem'.tr)),
        ],
      ),
    );
  }
}
