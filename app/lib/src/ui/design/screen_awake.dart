import 'package:flutter/foundation.dart';
import 'package:wakelock_plus/wakelock_plus.dart';

/// Mantém o ecrã ligado enquanto um ecrã o pede (o painel de cozinha). Onde o plugin não existe (testes, algumas
/// plataformas) não faz nada: o ecrã apaga-se como de costume, mas a app não falha.
class ScreenAwake {
  ScreenAwake._();

  /// Substituível nos testes.
  @visibleForTesting
  static Future<void> Function(bool on) toggle = (on) => WakelockPlus.toggle(enable: on);

  static Future<void> set(bool on) async {
    try {
      await toggle(on);
    } catch (_) {
      // Sem plugin nesta plataforma.
    }
  }
}
