import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'src/api/api_client.dart';
import 'src/config.dart';
import 'src/households/invite_link.dart';
import 'src/l10n/strings.dart';
import 'src/notifications/firebase_push.dart';
import 'src/notifications/notification_center.dart';
import 'src/notifications/push_registration.dart';
import 'src/realtime/household_realtime.dart';
import 'src/session/device_kind.dart';
import 'src/session/session_controller.dart';
import 'src/session/token_store.dart';
import 'src/shopping/shopping_repository.dart';
import 'src/storage/key_value_store.dart';
import 'src/ui/app.dart';

Future<void> main() async {
  // O Firebase (quando arrancar) e as preferências precisam dos canais da plataforma já prontos.
  WidgetsFlutterBinding.ensureInitialized();
  final preferences = PreferencesStore();
  await AppLanguage.restore(preferences);
  final api = ApiClient(baseUrl: AppConfig.apiUrl, deviceKind: currentDeviceKind());
  final session = SessionController(api: api, store: const SecureTokenStore())..restore();

  // Um link de convite (…/?convite=CÓDIGO) abre a app web com o código à espera.
  final invite = kIsWeb ? Uri.base.queryParameters['convite'] : null;
  session.pendingInvite = invite == null ? null : inviteCodeFrom(invite);
  final realtime = HouseholdRealtime(baseUrl: AppConfig.apiUrl, accessToken: session.freshAccessToken);
  final shopping = ShoppingRepository(api: api, store: preferences)..restore();
  final notifications = NotificationCenter(api: api, realtime: realtime);

  // A sessão acabou (aqui ou noutro lado): o que ficou guardado neste aparelho sai.
  session.onSessionEnded = () async {
    await realtime.disconnect();
    await shopping.clear();
    notifications.clear();
  };

  // A cópia das listas grava-se com um pequeno atraso; ao ir para segundo plano (ou fechar), grava-se já.
  AppLifecycleListener(onPause: shopping.flush, onHide: shopping.flush, onDetach: shopping.flush);

  // Avisos fora da app (Firebase, no Android e na web). O Firebase só arranca quando a pessoa entra (P8).
  final push = PushRegistration.lazy(api: api, create: createPushTokenSource);

  runApp(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
        Provider.value(value: realtime),
        ChangeNotifierProvider.value(value: shopping),
        ChangeNotifierProvider.value(value: notifications),
        Provider.value(value: push),
      ],
      child: const KubicoApp(),
    ),
  );
}
