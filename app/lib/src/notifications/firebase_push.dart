import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';

import '../../firebase_options.dart';
import 'push_registration.dart';

/// Os avisos fora da app chegam pelo Firebase Cloud Messaging (projeto kubico-casa). Só o Android e a web estão
/// configurados (ver firebase_options.dart); no resto, e se o Firebase não arrancar, ficam só no sino.
Future<PushTokenSource> createPushTokenSource() async {
  if (!kIsWeb && defaultTargetPlatform != TargetPlatform.android) return const NoPushTokenSource();
  try {
    await Firebase.initializeApp(options: DefaultFirebaseOptions.currentPlatform);
    return const FirebasePushTokenSource();
  } catch (e) {
    debugPrint('Kubico: Firebase indisponível, os avisos ficam só na app ($e)');
    return const NoPushTokenSource();
  }
}

/// O identificador deste telemóvel (ou browser) no Firebase Cloud Messaging: o Firebase Installation ID (FID),
/// que o servidor usa como destino. Com a app aberta, os avisos chegam pelo tempo real e aparecem no sino; com ela
/// fechada, o sistema mostra-os sozinho.
class FirebasePushTokenSource implements PushTokenSource {
  const FirebasePushTokenSource();

  FirebaseMessaging get _messaging => FirebaseMessaging.instance;

  @override
  String? get platform => kIsWeb ? 'web' : 'android';

  /// No Android 13 ou mais recente (e no browser) pede autorização para mostrar notificações.
  @override
  Future<String?> getToken() async {
    final settings = await _messaging.requestPermission();
    if (settings.authorizationStatus == AuthorizationStatus.denied) return null;

    // O registo é assíncrono: o FID chega pelo onRegistered (ouve-se antes de pedir, para não o perder).
    final registered = _messaging.onRegistered.first.timeout(const Duration(seconds: 30));
    await _messaging.register();
    return registered;
  }

  /// Se o Firebase voltar a registar o dispositivo com outro FID, o novo também tem de chegar ao servidor.
  @override
  Stream<String> get onTokenRefresh => _messaging.onRegistered;

  @override
  Future<void> deleteToken() => _messaging.unregister();
}
