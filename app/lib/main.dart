import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'src/ui/design/components.dart' show AvatarPhotos;
import 'src/api/api_client.dart';
import 'src/config.dart';
import 'src/households/invite_link.dart';
import 'src/l10n/strings.dart';
import 'src/notifications/firebase_push.dart';
import 'src/notifications/notification_center.dart';
import 'src/notifications/push_registration.dart';
import 'src/realtime/household_realtime.dart';
import 'src/offline/chore_queue.dart';
import 'src/offline/response_cache.dart';
import 'src/session/child_device.dart';
import 'src/session/device_kind.dart';
import 'src/session/session_controller.dart';
import 'src/session/token_store.dart';
import 'src/shopping/shopping_order.dart';
import 'src/shopping/shopping_repository.dart';
import 'src/storage/key_value_store.dart';
import 'src/ui/app.dart';
import 'src/ui/first_steps.dart';
import 'src/ui/design/reading_mode.dart';

Future<void> main() async {
  // O Firebase (quando arrancar) e as preferências precisam dos canais da plataforma já prontos.
  WidgetsFlutterBinding.ensureInitialized();
  final preferences = PreferencesStore();
  await AppLanguage.restore(preferences);
  await ReadingMode.restore(preferences);
  await ChildDevice.restore(preferences);
  await ShoppingOrder.restore(preferences);
  await Tips.restore(preferences);
  // A última cópia de cada leitura, para abrir sem rede (F4-14).
  final responses = ResponseCache(store: preferences);
  await responses.restore();
  final api = ApiClient(baseUrl: AppConfig.apiUrl, deviceKind: currentDeviceKind())..cache = responses;
  final session = SessionController(api: api, store: const SecureTokenStore());
  final restoringSession = session.restore();

  // Um link de convite (…/?convite=CÓDIGO) abre a app web com o código à espera.
  final invite = kIsWeb ? Uri.base.queryParameters['convite'] : null;
  session.pendingInvite = invite == null ? null : inviteCodeFrom(invite);
  final realtime = HouseholdRealtime(baseUrl: AppConfig.apiUrl, accessToken: session.freshAccessToken);
  final shopping = ShoppingRepository(api: api, store: preferences)..restore();
  // A fila das compras só se envia com a sessão pronta (antes, saía sem token, levava 401 e perdia-se).
  final chores = ChoreQueue(api: api, store: preferences);
  await chores.restore();
  unawaited(restoringSession.whenComplete(shopping.sync));
  unawaited(restoringSession.whenComplete(chores.sync));
  final notifications = NotificationCenter(api: api, realtime: realtime);
  // A rede voltou (o tempo real voltou a ligar): o que ficou feito sem rede vai já.
  realtime.reconnected.listen((_) => unawaited(chores.sync()));

  // A sessão acabou (aqui ou noutro lado): o que ficou guardado neste aparelho sai.
  session.onSessionEnded = () async {
    await realtime.disconnect();
    await shopping.clear();
    await chores.clear();
    await responses.clear();
    notifications.clear();
    AvatarPhotos.clear();
  };

  // A cópia das listas grava-se com um pequeno atraso; ao ir para segundo plano (ou fechar), grava-se já.
  Future<void> flushAll() => Future.wait([shopping.flush(), responses.flush()]);
  AppLifecycleListener(
    onPause: flushAll,
    onHide: flushAll,
    onDetach: flushAll,
    // Ao voltar: a ligação de tempo real pode ter caído e a sessão pode ter aberto sem servidor.
    onResume: () {
      unawaited(realtime.resume());
      unawaited(session.retryIfOffline().then((_) => Future.wait([shopping.sync(), chores.sync()])));
    },
  );

  // Avisos fora da app (Firebase, no Android e na web). O Firebase só arranca quando a pessoa entra (P8).
  final push = PushRegistration.lazy(api: api, create: createPushTokenSource);

  runApp(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
        Provider.value(value: realtime),
        ChangeNotifierProvider.value(value: shopping),
        ChangeNotifierProvider.value(value: chores),
        ChangeNotifierProvider.value(value: notifications),
        Provider.value(value: push),
      ],
      child: const KubicoApp(),
    ),
  );
}
