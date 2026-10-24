import 'package:flutter/foundation.dart';

/// Endereço da API. Pode ser definido no arranque:
///   flutter run --dart-define=API_URL=https://api.kubico.pt
class AppConfig {
  static const _fromEnvironment = String.fromEnvironment('API_URL');
  static const _webUrl = String.fromEnvironment('WEB_URL');

  /// Onde está a app web, para os links de convite. Na web é o próprio endereço; nas apps nativas, só com
  /// --dart-define=WEB_URL=https://app.kubico.pt (sem ele, os convites partilham só o código).
  static String? get webUrl {
    if (_webUrl.isNotEmpty) return _webUrl;
    return kIsWeb ? Uri.base.origin : null;
  }

  static String get apiUrl {
    if (_fromEnvironment.isNotEmpty) return _fromEnvironment;

    // O emulador Android chega ao PC através de 10.0.2.2, não de localhost.
    if (!kIsWeb && defaultTargetPlatform == TargetPlatform.android) {
      return 'http://10.0.2.2:5281';
    }
    return 'http://localhost:5281';
  }
}
