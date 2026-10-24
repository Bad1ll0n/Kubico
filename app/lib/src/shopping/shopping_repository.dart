import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../storage/key_value_store.dart';

enum ShoppingChangeType { add, check, delete, clear }

/// Uma alteração a uma lista, à espera de chegar ao servidor. Enviam-se por ordem.
class ShoppingChange {
  const ShoppingChange({
    required this.type,
    required this.householdId,
    required this.listId,
    this.itemId,
    this.name,
    this.quantity,
    this.category,
    this.barcode,
    this.isChecked,
    required this.at,
  });

  final ShoppingChangeType type;
  final String householdId;
  final String listId;
  final String? itemId;
  final String? name;
  final String? quantity;
  final String? category;
  final String? barcode;
  final bool? isChecked;
  final DateTime at;

  /// O artigo como aparece na lista enquanto o servidor não o confirma.
  ShoppingItem toItem() => ShoppingItem(
    id: itemId!,
    listId: listId,
    name: name!,
    quantity: quantity,
    category: category,
    isChecked: false,
    addedByUserId: '',
    createdAt: at,
    updatedAt: at,
    barcode: barcode,
  );

  Map<String, dynamic> toJson() => {
    'type': type.name,
    'householdId': householdId,
    'listId': listId,
    if (itemId != null) 'itemId': itemId,
    if (name != null) 'name': name,
    if (quantity != null) 'quantity': quantity,
    if (category != null) 'category': category,
    if (barcode != null) 'barcode': barcode,
    if (isChecked != null) 'isChecked': isChecked,
    'at': at.toIso8601String(),
  };

  factory ShoppingChange.fromJson(Map<String, dynamic> json) => ShoppingChange(
    type: ShoppingChangeType.values.byName(json['type'] as String),
    householdId: json['householdId'] as String,
    listId: json['listId'] as String,
    itemId: json['itemId'] as String?,
    name: json['name'] as String?,
    quantity: json['quantity'] as String?,
    category: json['category'] as String?,
    barcode: json['barcode'] as String?,
    isChecked: json['isChecked'] as bool?,
    at: DateTime.parse(json['at'] as String),
  );
}

/// Listas de compras que funcionam sem rede (no supermercado a rede falha muitas vezes).
///
/// Guarda no dispositivo a última cópia das listas e dos artigos e uma fila das alterações feitas. Cada alteração
/// aparece logo e é enviada por ordem quando houver ligação; até lá, a lista mostra a cópia com as alterações
/// por cima. Os artigos novos levam um id gerado aqui, por isso reenviar não os duplica.
///
/// A cópia dos artigos fica também em memória: cada alteração confirmada muda a memória e a gravação no dispositivo
/// faz-se [saveDelay] depois, uma vez para várias alterações seguidas (P11; antes, cada toque lia, descodificava e
/// voltava a gravar o JSON da lista toda, ~100 KB numa lista de 300 artigos). A fila grava-se sempre logo: é ela que
/// não pode perder nada. Ao sair da app (ou da sessão), o que falta gravar grava-se já ([flush]).
class ShoppingRepository extends ChangeNotifier {
  ShoppingRepository({
    required this.api,
    required KeyValueStore store,
    this.retryEvery = const Duration(seconds: 30),
    this.saveDelay = const Duration(seconds: 1),
  }) : _store = store; // ignore: prefer_initializing_formals

  static const _pendingKey = 'kubico.shopping.pending';
  static const _cacheKeysKey = 'kubico.shopping.cacheKeys';

  final ApiClient api;
  final KeyValueStore _store;

  /// De quanto em quanto tempo se volta a tentar enviar quando não há rede.
  final Duration retryEvery;

  /// Quanto tempo se espera para gravar a cópia dos artigos depois de uma alteração (junta as seguintes). Zero grava
  /// logo (nos testes).
  final Duration saveDelay;

  final _pending = <ShoppingChange>[];
  final _cacheKeys = <String>{};
  var _offline = false;
  Future<void>? _syncing;
  Timer? _retry;

  /// A cópia dos artigos de cada lista em memória (a mesma que está gravada, mais as alterações por gravar).
  final _itemsCache = <String, Map<String, ShoppingItem>>{};

  /// As listas com alterações à cópia ainda por gravar.
  final _unsaved = <String>{};
  Timer? _saveTimer;

  /// O último pedido falhou por falta de ligação.
  bool get isOffline => _offline;

  /// Alterações ainda por enviar.
  int get pendingCount => _pending.length;

  /// Lê a fila guardada (alterações feitas antes de a app fechar) e tenta enviá-la.
  Future<void> restore() async {
    final pending = await _store.read(_pendingKey);
    final keys = await _store.read(_cacheKeysKey);
    _pending
      ..clear()
      ..addAll([for (final c in _decodeList(pending)) ShoppingChange.fromJson(c as Map<String, dynamic>)]);
    _cacheKeys
      ..clear()
      ..addAll(_decodeList(keys).cast<String>());
    notifyListeners();
    if (_pending.isNotEmpty) unawaited(sync());
  }

  Future<List<ShoppingList>> lists(String householdId) async {
    final key = 'kubico.shopping.lists.$householdId';
    try {
      final lists = await api.getLists(householdId);
      _setOffline(false);
      await _cache(key, [for (final l in lists) l.toJson()]);
      return lists;
    } on ApiException catch (e) {
      final cached = await _store.read(key);
      if (!_isNetwork(e) || cached == null) rethrow;
      _setOffline(true);
      return [for (final l in _decodeList(cached)) ShoppingList.fromJson(l as Map<String, dynamic>)];
    }
  }

  /// Os artigos de uma lista: do servidor quando há rede, senão da cópia guardada; sempre com as alterações
  /// por enviar aplicadas por cima.
  Future<List<ShoppingItem>> items(String householdId, String listId) async {
    await sync();
    try {
      final items = await api.getItems(householdId, listId);
      _setOffline(false);
      _itemsCache[listId] = {for (final i in items) i.id: i};
      _unsaved.remove(listId);
      await _saveItems(listId, items);
      return _withPending(listId, items);
    } on ApiException catch (e) {
      final cached = await _cachedItems(listId);
      if (!_isNetwork(e) || cached == null) rethrow;
      _setOffline(true);
      return _withPending(listId, cached.values.toList());
    }
  }

  ShoppingItem addItem(
    String householdId,
    String listId, {
    required String name,
    String? quantity,
    String? category,
    String? barcode,
  }) {
    final change = ShoppingChange(
      type: ShoppingChangeType.add,
      householdId: householdId,
      listId: listId,
      itemId: newItemId(),
      name: name,
      quantity: quantity,
      category: category,
      barcode: barcode,
      at: DateTime.now().toUtc(),
    );
    _enqueue(change);
    return change.toItem();
  }

  void setChecked(String householdId, String listId, String itemId, bool isChecked) => _enqueue(
    ShoppingChange(
      type: ShoppingChangeType.check,
      householdId: householdId,
      listId: listId,
      itemId: itemId,
      isChecked: isChecked,
      at: DateTime.now().toUtc(),
    ),
  );

  void deleteItem(String householdId, String listId, String itemId) => _enqueue(
    ShoppingChange(
      type: ShoppingChangeType.delete,
      householdId: householdId,
      listId: listId,
      itemId: itemId,
      at: DateTime.now().toUtc(),
    ),
  );

  void clearChecked(String householdId, String listId) => _enqueue(
    ShoppingChange(
      type: ShoppingChangeType.clear,
      householdId: householdId,
      listId: listId,
      at: DateTime.now().toUtc(),
    ),
  );

  /// Envia as alterações por ordem. Sem rede, pára e volta a tentar mais tarde; uma alteração que o servidor
  /// recusa (por exemplo, o artigo já foi apagado por outra pessoa) é descartada.
  Future<void> sync() => _syncing ??= _flush().whenComplete(() => _syncing = null);

  /// Grava já a cópia dos artigos que ainda está só em memória (ao sair da app).
  Future<void> flush() async {
    _saveTimer?.cancel();
    _saveTimer = null;
    final lists = [..._unsaved];
    _unsaved.clear();
    for (final listId in lists) {
      final items = _itemsCache[listId];
      if (items != null) await _saveItems(listId, items.values);
    }
  }

  /// Ao terminar a sessão: esquece a fila e as cópias (são dados da casa).
  Future<void> clear() async {
    _retry?.cancel();
    _retry = null;
    _saveTimer?.cancel();
    _saveTimer = null;
    _unsaved.clear();
    _itemsCache.clear();
    _pending.clear();
    for (final key in _cacheKeys) {
      await _store.write(key, null);
    }
    _cacheKeys.clear();
    await _store.write(_pendingKey, null);
    await _store.write(_cacheKeysKey, null);
    _offline = false;
    notifyListeners();
  }

  @override
  void dispose() {
    _retry?.cancel();
    unawaited(flush());
    super.dispose();
  }

  Future<void> _flush() async {
    while (_pending.isNotEmpty) {
      final change = _pending.first;
      try {
        await _send(change);
        _setOffline(false);
      } on ApiException catch (e) {
        if (_isNetwork(e)) {
          _setOffline(true);
          _retry ??= Timer.periodic(retryEvery, (_) => sync());
          return;
        }
        debugPrint('Kubico: alteração à lista recusada (${e.message})');
      }
      _pending.removeAt(0);
      await _savePending();
      notifyListeners();
    }
    _retry?.cancel();
    _retry = null;
  }

  Future<void> _send(ShoppingChange change) async {
    final (householdId, listId) = (change.householdId, change.listId);
    switch (change.type) {
      case ShoppingChangeType.add:
        final item = await api.addItem(
          householdId,
          listId,
          id: change.itemId,
          name: change.name!,
          quantity: change.quantity,
          category: change.category,
          barcode: change.barcode,
        );
        await _updateCachedItems(listId, (items) => items..[item.id] = item);
      case ShoppingChangeType.check:
        final item = await api.setItemChecked(householdId, listId, change.itemId!, change.isChecked!);
        await _updateCachedItems(listId, (items) => items..[item.id] = item);
      case ShoppingChangeType.delete:
        try {
          await api.deleteItem(householdId, listId, change.itemId!);
        } on ApiException catch (e) {
          if (e.statusCode != 404) rethrow; // Já tinha sido apagado: é o que se queria.
        }
        await _updateCachedItems(listId, (items) => items..remove(change.itemId));
      case ShoppingChangeType.clear:
        await api.clearChecked(householdId, listId);
        await _updateCachedItems(listId, (items) => items..removeWhere((_, i) => i.isChecked));
    }
  }

  void _enqueue(ShoppingChange change) {
    _pending.add(change);
    notifyListeners();
    unawaited(_savePending().then((_) => sync()));
  }

  List<ShoppingItem> _withPending(String listId, List<ShoppingItem> items) {
    final byId = {for (final i in items) i.id: i};
    for (final change in _pending.where((c) => c.listId == listId)) {
      switch (change.type) {
        case ShoppingChangeType.add:
          byId[change.itemId!] ??= change.toItem();
        case ShoppingChangeType.check:
          final item = byId[change.itemId];
          if (item != null) byId[item.id] = item.copyWith(isChecked: change.isChecked);
        case ShoppingChangeType.delete:
          byId.remove(change.itemId);
        case ShoppingChangeType.clear:
          byId.removeWhere((_, i) => i.isChecked);
      }
    }
    return byId.values.toList();
  }

  void _setOffline(bool offline) {
    if (_offline == offline) return;
    _offline = offline;
    notifyListeners();
  }

  static bool _isNetwork(ApiException e) => e.statusCode == 0;

  static String _itemsKey(String listId) => 'kubico.shopping.items.$listId';

  Future<void> _saveItems(String listId, Iterable<ShoppingItem> items) =>
      _cache(_itemsKey(listId), [for (final i in items) i.toJson()]);

  /// Mantém a cópia certa depois de enviar uma alteração (para continuar certa se a rede voltar a falhar): muda-a em
  /// memória e grava-a um pouco depois, uma vez para várias alterações seguidas.
  Future<void> _updateCachedItems(
    String listId,
    Map<String, ShoppingItem> Function(Map<String, ShoppingItem>) apply,
  ) async {
    final items = await _cachedItems(listId);
    if (items == null) return;
    apply(items);
    _unsaved.add(listId);
    if (saveDelay == Duration.zero) {
      await flush();
      return;
    }
    _saveTimer ??= Timer(saveDelay, () {
      _saveTimer = null;
      unawaited(flush());
    });
  }

  /// A cópia dos artigos de uma lista: da memória ou, da primeira vez, do que está gravado no dispositivo.
  Future<Map<String, ShoppingItem>?> _cachedItems(String listId) async {
    final inMemory = _itemsCache[listId];
    if (inMemory != null) return inMemory;
    final stored = await _store.read(_itemsKey(listId));
    if (stored == null) return null;
    return _itemsCache[listId] = {for (final i in _decodeItems(stored)) i.id: i};
  }

  Future<void> _cache(String key, Object value) async {
    await _store.write(key, jsonEncode(value));
    if (_cacheKeys.add(key)) await _store.write(_cacheKeysKey, jsonEncode(_cacheKeys.toList()));
  }

  Future<void> _savePending() => _store.write(_pendingKey, jsonEncode([for (final c in _pending) c.toJson()]));

  static List<dynamic> _decodeList(String? json) => json == null ? const [] : jsonDecode(json) as List<dynamic>;

  static List<ShoppingItem> _decodeItems(String json) => [
    for (final i in _decodeList(json)) ShoppingItem.fromJson(i as Map<String, dynamic>),
  ];
}

/// Um UUID v4, para o artigo ter id antes de chegar ao servidor.
String newItemId() {
  final random = Random.secure();
  final bytes = List<int>.generate(16, (_) => random.nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-'
      '${hex.substring(16, 20)}-${hex.substring(20)}';
}
