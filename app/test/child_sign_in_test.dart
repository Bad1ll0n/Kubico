import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/auth_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

void main() {
  testWidgets('uma criança entra com o código da família, o seu nome e o PIN', (tester) async {
    final logins = <Map<String, dynamic>>[];
    final server = MockClient((request) async {
      switch (request.url.path) {
        case '/api/auth/child/profiles':
          return _json([
            {'userId': 'rita', 'displayName': 'Rita', 'color': '#E07A5F'},
            {'userId': 'tiago', 'displayName': 'Tiago', 'color': '#81B29A'},
          ]);
        case '/api/auth/child/login':
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          logins.add(body);
          if (body['pin'] != '1234') return _json({'title': 'PIN errado.', 'status': 401}, 401);
          return _json({
            'accessToken': 'crianca',
            'expiresAt': DateTime.now().add(const Duration(minutes: 15)).toUtc().toIso8601String(),
            'user': {'id': 'rita', 'email': null, 'displayName': 'Rita', 'isChildProfile': true},
            'refreshToken': 'r',
            'refreshTokenExpiresAt': DateTime.now().add(const Duration(days: 60)).toUtc().toIso8601String(),
          });
        case '/api/me':
          return _json({
            'user': {'id': 'rita', 'email': null, 'displayName': 'Rita', 'isChildProfile': true},
            'households': [
              {'id': 'h1', 'name': 'Casa', 'role': 'Child'},
            ],
          });
      }
      return http.Response('', 404);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: server);
    final session = SessionController(api: api, store: MemoryTokenStore());

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
        ],
        child: const MaterialApp(home: AuthScreen()),
      ),
    );

    // Um telemóvel ao alto: o teclado do PIN cabe no ecrã.
    tester.view.physicalSize = const Size(800, 1800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pump();

    await tester.tap(find.text('Entrar com perfil de criança'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Código da família'), 'famil-ia23');
    await tester.tap(find.text('Continuar'));
    await tester.pumpAndSettle();

    expect(find.text('Quem és tu?'), findsOneWidget);
    await tester.tap(find.text('Rita'));
    await tester.pumpAndSettle();
    expect(find.text('Olá, Rita!'), findsOneWidget);

    for (final d in '0000'.split('')) {
      await tester.tap(find.byKey(ValueKey('pin-key-$d')));
    }
    await tester.pump();
    await tester.tap(find.byKey(const ValueKey('pin-key-ok')));
    await tester.pumpAndSettle();
    expect(find.text('PIN errado.'), findsOneWidget);
    expect(find.byKey(const ValueKey('pin-dots-0')), findsOneWidget, reason: 'o PIN errado apaga-se');

    for (final d in '1234'.split('')) {
      await tester.tap(find.byKey(ValueKey('pin-key-$d')));
    }
    await tester.pump();
    expect(find.byKey(const ValueKey('pin-dots-4')), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('pin-key-ok')));
    await tester.pumpAndSettle();

    expect(logins.last, {'familyCode': 'famil-ia23', 'userId': 'rita', 'pin': '1234'});
    expect(session.status, SessionStatus.signedIn);
    expect(session.user!.isChildProfile, isTrue);
    expect(session.currentHousehold!.name, 'Casa');
    expect(find.text('Entrar como criança'), findsNothing, reason: 'o ecrã fecha depois de entrar');
  });
}
