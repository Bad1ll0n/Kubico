import 'package:flutter/foundation.dart';

/// O tipo de dispositivo em que a app corre. Vai num cabeçalho para a API, para a lista de sessões dizer onde
/// se entrou (só ASCII: os cabeçalhos HTTP não aceitam acentos).
String? currentDeviceKind() {
  if (kIsWeb) return 'web';
  return switch (defaultTargetPlatform) {
    TargetPlatform.android => 'android',
    TargetPlatform.iOS => 'ios',
    TargetPlatform.macOS => 'macos',
    TargetPlatform.windows => 'windows',
    TargetPlatform.linux => 'linux',
    TargetPlatform.fuchsia => null,
  };
}
