import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';

import '../storage/key_value_store.dart';

/// A última resposta de cada leitura da API, guardada no aparelho (F4-14): sem rede, Hoje, Tarefas, Calendário,
/// Recompensas e a casa abrem com a última cópia em vez de um erro. As compras têm a sua própria cópia e fila
/// (ShoppingRepository); a sessão guarda o /api/me à parte.
///
/// Fica em memória e grava-se no dispositivo [saveDelay] depois da última alteração (uma escrita para várias). Guarda
/// no máximo [maxEntries] respostas (sai a usada há mais tempo). Apaga-se quando a sessão acaba.
class ResponseCache {
  ResponseCache({required KeyValueStore store, this.saveDelay = const Duration(seconds: 1), this.maxEntries = 300})
    : _store = store; // ignore: prefer_initializing_formals

  static const _storageKey = 'kubico.offline.responses';

  final KeyValueStore _store;
  final Duration saveDelay;
  final int maxEntries;

  /// Caminho → corpo da resposta (JSON), da usada há mais tempo para a mais recente.
  final _entries = <String, String>{}; // um Map literal mantém a ordem de inserção
  Timer? _save;

  /// As leituras que se guardam: as da casa e dos avisos. Fora ficam a sessão, a conta e as compras.
  static bool isCacheable(String path) =>
      (path.startsWith('/api/households/') || path.startsWith('/api/notifications')) &&
      !path.contains('/lists') &&
      !path.contains('/photos/') &&
      !path.contains('/products/');

  Future<void> restore() async {
    try {
      final saved = await _store.read(_storageKey);
      if (saved == null) return;
      final map = jsonDecode(saved) as Map<String, dynamic>;
      _entries
        ..clear()
        ..addAll({for (final e in map.entries) e.key: e.value as String});
    } catch (e) {
      debugPrint('Kubico: a cópia sem rede não se conseguiu ler ($e)');
    }
  }

  String? read(String path) {
    final body = _entries.remove(path);
    if (body != null) _entries[path] = body;
    return body;
  }

  void write(String path, String body) {
    _entries
      ..remove(path)
      ..[path] = body;
    while (_entries.length > maxEntries) {
      _entries.remove(_entries.keys.first);
    }
    _save?.cancel();
    _save = Timer(saveDelay, flush);
  }

  Future<void> flush() async {
    _save?.cancel();
    _save = null;
    try {
      await _store.write(_storageKey, jsonEncode(_entries));
    } catch (e) {
      debugPrint('Kubico: a cópia sem rede não se conseguiu gravar ($e)');
    }
  }

  /// A sessão acabou: nada do que era desta pessoa fica no aparelho.
  Future<void> clear() async {
    _save?.cancel();
    _save = null;
    _entries.clear();
    try {
      await _store.write(_storageKey, null);
    } catch (_) {
      // Fica até se conseguir apagar.
    }
  }
}
