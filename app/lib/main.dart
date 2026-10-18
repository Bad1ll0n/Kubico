import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'src/api/api_client.dart';
import 'src/config.dart';
import 'src/households/invite_link.dart';
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

void main() {
  // O Firebase arranca antes do runApp e precisa dos canais da plataforma já prontos.
  WidgetsFlutterBinding.ensureInitialized();
  final api = ApiClient(baseUrl: AppConfig.apiUrl, deviceKind: currentDeviceKind());
  final session = SessionController(api: api, store: const SecureTokenStore())..restore();

  // Um link de convite (…/?convite=CÓDIGO) abre a app web com o código à espera.
  final invite = kIsWeb ? Uri.base.queryParameters['convite'] : null;
  session.pendingInvite = invite == null ? null : inviteCodeFrom(invite);
  final realtime = HouseholdRealtime(baseUrl: AppConfig.apiUrl, accessToken: session.freshAccessToken);
  final shopping = ShoppingRepository(api: api, store: PreferencesStore())..restore();
  final notifications = NotificationCenter(api: api, realtime: realtime);

  // Avisos fora da app (Firebase, no Android e na web). Arranca em paralelo para não atrasar a abertura da app.
  final push = PushRegistration(api: api, source: createPushTokenSource());

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
