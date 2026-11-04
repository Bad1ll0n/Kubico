import 'package:flutter/material.dart';

import '../../l10n/strings.dart';
import '../../storage/key_value_store.dart';

/// O tamanho de leitura da app neste aparelho: normal, ou "conforto" (letra 15 % maior por cima da escolhida no
/// sistema e alvos de toque maiores), para avós, crianças pequenas e um tablet na cozinha.
enum ReadingMode {
  normal('Normal', 1),
  comfort('Conforto', 1.15);

  const ReadingMode(this._label, this.textFactor);

  final String _label;

  /// O multiplicador da letra, por cima do tamanho escolhido no sistema.
  final double textFactor;

  String get label => _label.tr;

  static const _storageKey = 'kubico.readingMode';

  /// O modo atual; a app ouve-o e desenha-se outra vez quando muda.
  static final current = ValueNotifier<ReadingMode>(ReadingMode.normal);

  static KeyValueStore? _store;

  /// Lê o modo guardado no dispositivo (normal se nunca se escolheu).
  static Future<void> restore(KeyValueStore store) async {
    _store = store;
    try {
      final saved = await store.read(_storageKey);
      current.value = ReadingMode.values.where((m) => m.name == saved).firstOrNull ?? ReadingMode.normal;
    } catch (_) {
      // Sem acesso ao armazenamento: fica o normal.
    }
  }

  static Future<void> set(ReadingMode mode) async {
    current.value = mode;
    try {
      await _store?.write(_storageKey, mode.name);
    } catch (_) {
      // Não ficou guardado: no próximo arranque volta ao anterior.
    }
  }

  /// Aplica o modo a [child]: multiplica a escala de texto do sistema (no máximo 2×, o que os ecrãs aguentam) e, no
  /// conforto, dá alvos de toque maiores.
  Widget apply(BuildContext context, Widget child) {
    if (this == ReadingMode.normal) return child;
    final media = MediaQuery.of(context);
    final scaled = media.textScaler.scale(16) / 16 * textFactor;
    final theme = Theme.of(context);
    return MediaQuery(
      data: media.copyWith(textScaler: TextScaler.linear(scaled.clamp(1, 2))),
      child: Theme(
        data: theme.copyWith(visualDensity: const VisualDensity(vertical: 2)),
        child: child,
      ),
    );
  }
}
