import 'package:flutter/foundation.dart';

import '../storage/key_value_store.dart';

/// Ordenar as categorias da lista pela ordem em que se costuma apanhar as coisas no supermercado (aprendida com o que
/// se marca, F4-22c), ou pela ordem por omissão (alfabética). Fica no aparelho; por omissão, a aprendida.
abstract final class ShoppingOrder {
  static const _key = 'kubico.shopping.learnedOrder';

  static final learned = ValueNotifier<bool>(true);
  static KeyValueStore? _store;

  static Future<void> restore(KeyValueStore store) async {
    _store = store;
    try {
      learned.value = await store.read(_key) != 'false';
    } catch (_) {
      // Fica a aprendida.
    }
  }

  static Future<void> set(bool value) async {
    learned.value = value;
    try {
      await _store?.write(_key, value ? null : 'false');
    } catch (_) {
      // Volta à anterior no próximo arranque.
    }
  }
}
