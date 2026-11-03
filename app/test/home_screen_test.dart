import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/notifications/notification_center.dart';
import 'package:kubico/src/notifications/push_registration.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/home_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Uma casa com uma pessoa (adulta ou criança); o resto das áreas responde 404 (os ecrãs mostram o erro).
Future<HouseholdRealtime> _pumpHome(
  WidgetTester tester, {
  required bool child,
  Size size = const Size(390, 800),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final server = MockClient((request) async {
    if (request.url.path == '/api/me') {
      return _json({
        'user': {
          'id': 'u1',
          'email': child ? null : 'mae@kubico.test',
          'displayName': child ? 'Rita' : 'Mãe',
          'isChildProfile': child,
        },
        'households': [
          {'id': 'h1', 'name': 'Casa da Avó Lurdes', 'role': child ? 'Child' : 'Admin'},
        ],
      });
    }
    return http.Response('', 404);
  });
  final api = ApiClient(baseUrl: 'http://api', httpClient: server)..accessToken = 't';
  final session = SessionController(api: api, store: MemoryTokenStore());
  await session.refresh();
  final realtime = HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't');
  final shopping = ShoppingRepository(api: api, store: MemoryKeyValueStore(), saveDelay: Duration.zero);
  addTearDown(shopping.dispose);
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
        ChangeNotifierProvider.value(value: shopping),
        Provider.value(value: realtime),
        ChangeNotifierProvider(
          create: (_) => NotificationCenter(api: api, realtime: realtime),
        ),
        Provider.value(
          value: PushRegistration(api: api, source: const NoPushTokenSource()),
        ),
      ],
      child: const MaterialApp(home: HomeScreen()),
    ),
  );
  await tester.pumpAndSettle();
  return realtime;
}

/// Fecha o ecrã e o tempo real (que tenta voltar a ligar), para não ficarem temporizadores pendentes.
Future<void> _close(WidgetTester tester, HouseholdRealtime realtime) async {
  await tester.pumpWidget(const SizedBox());
  await realtime.disconnect();
  await tester.pump(const Duration(minutes: 2));
}

void main() {
  testWidgets('no telemóvel, "Membros e convites" vai para o menu e o nome da casa fica com o espaço', (tester) async {
    final realtime = await _pumpHome(tester, child: false);

    expect(find.text('Casa da Avó Lurdes'), findsOneWidget);
    expect(find.byTooltip('Membros e convites'), findsNothing);
    await tester.tap(find.byType(PopupMenuButton<String>).last);
    await tester.pumpAndSettle();
    expect(find.text('Membros e convites'), findsOneWidget);
    expect(find.text('Terminar sessão em todos os dispositivos'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await _close(tester, realtime);
  });

  testWidgets('o menu de uma criança só tem o que é dela e "Mudar de criança"', (tester) async {
    final realtime = await _pumpHome(tester, child: true, size: const Size(1000, 800));

    expect(find.byTooltip('Membros e convites'), findsOneWidget, reason: 'num ecrã largo fica na barra');
    await tester.tap(find.byType(PopupMenuButton<String>).last);
    await tester.pumpAndSettle();
    expect(find.text('Mudar de criança'), findsOneWidget);
    expect(find.text('Terminar sessão'), findsNothing);
    expect(find.text('Dispositivos com sessão iniciada'), findsNothing);
    expect(find.text('Histórico de alterações'), findsNothing);
    expect(find.text('Terminar sessão em todos os dispositivos'), findsNothing);
    await _close(tester, realtime);
  });
}
