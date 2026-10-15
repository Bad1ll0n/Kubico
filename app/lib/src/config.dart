import 'package:flutter/foundation.dart';

/// Endereço da API. Pode ser definido no arranque:
///   flutter run --dart-define=API_URL=https://api.kubico.pt
class AppConfig {
  static const _fromEnvironment = String.fromEnvironment('API_URL');

  static String get apiUrl {
    if (_fromEnvironment.isNotEmpty) return _fromEnvironment;

    // O emulador Android chega ao PC através de 10.0.2.2, não de localhost.
    if (!kIsWeb && defaultTargetPlatform == TargetPlatform.android) {
      return 'http://10.0.2.2:5281';
    }
    return 'http://localhost:5281';
  }
}
