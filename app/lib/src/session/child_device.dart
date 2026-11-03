import 'package:flutter/foundation.dart';

import '../storage/key_value_store.dart';

/// Um tablet ou telemóvel partilhado pelas crianças (UX-11): depois da primeira entrada com o código da família, o
/// aparelho lembra-o e a entrada abre logo em "Quem és tu?". "Mudar de criança" termina a sessão e volta a esse ecrã.
abstract final class ChildDevice {
  static const _codeKey = 'kubico.childFamilyCode';

  /// O código da família lembrado neste aparelho (null se nenhuma criança entrou aqui, ou se se esqueceu).
  static final familyCode = ValueNotifier<String?>(null);

  /// Pedido por "Mudar de criança": o ecrã de entrada abre logo a entrada das crianças.
  static var openPickerOnSignIn = false;

  static KeyValueStore? _store;

  static Future<void> restore(KeyValueStore store) async {
    _store = store;
    try {
      familyCode.value = await store.read(_codeKey);
    } catch (_) {
      // Sem acesso ao armazenamento: pede-se o código.
    }
  }

  static Future<void> remember(String code) async {
    familyCode.value = code;
    try {
      await _store?.write(_codeKey, code);
    } catch (_) {
      // Não ficou guardado: na próxima vez pede-se o código.
    }
  }

  static Future<void> forget() async {
    familyCode.value = null;
    try {
      await _store?.write(_codeKey, null);
    } catch (_) {
      // Fica até se conseguir apagar.
    }
  }
}
