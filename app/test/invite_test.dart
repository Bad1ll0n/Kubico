import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/households/invite_link.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/household_screen.dart';
import 'package:kubico/src/ui/onboarding_screen.dart';
import 'package:provider/provider.dart';
import 'package:qr_flutter/qr_flutter.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

final _server = MockClient((request) async {
  switch ((request.method, request.url.path)) {
    case ('GET', '/api/me'):
      return _json({
        'user': {'id': 'mae', 'email': 'mae@kubico.test', 'displayName': 'Mãe', 'isChildProfile': false},
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
        ],
      });
    case ('GET', '/api/households/h1'):
      return _json({
        'id': 'h1',
        'name': 'Casa',
        'timeZoneId': 'Europe/Lisbon',
        'myRole': 'Admin',
        'members': [
          {'userId': 'mae', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#4F7CAC'},
        ],
      });
    case ('GET', '/api/households/h1/absences'):
      return _json(<Object>[]);
    case ('GET', '/api/households/h1/invites'):
      return _json(<Object>[]);
    case ('POST', '/api/households/h1/invites'):
      return _json({'code': 'K7M2QX9P', 'role': 'Adult', 'expiresAt': '2026-10-25T10:00:00Z'});
  }
  return http.Response('', 404);
});

Future<SessionController> _session() async {
  final api = ApiClient(baseUrl: 'http://api', httpClient: _server)..accessToken = 't';
  final session = SessionController(api: api, store: MemoryTokenStore());
  await session.refresh();
  return session;
}

Widget _app(SessionController session, Widget home) => MultiProvider(
  providers: [
    Provider.value(value: session.api),
    ChangeNotifierProvider.value(value: session),
    Provider.value(
      value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
    ),
  ],
  child: MaterialApp(home: home),
);

void main() {
  test('o código de um convite lê-se de um link, de um QR ou escrito à mão', () {
    expect(inviteCodeFrom('K7M2QX9P'), 'K7M2QX9P');
    expect(inviteCodeFrom(' k7m2-qx9p '), 'K7M2QX9P');
    expect(inviteCodeFrom('https://app.kubico.pt/?convite=k7m2qx9p'), 'K7M2QX9P');
    expect(inviteCodeFrom('https://exemplo.pt/outra-coisa'), isNull);
    expect(inviteCodeFrom('olá'), isNull);
    expect(inviteLink('K7M2QX9P', webUrl: 'https://app.kubico.pt/'), 'https://app.kubico.pt/?convite=K7M2QX9P');
  });

  testWidgets('o convite mostra o QR e o código', (tester) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final session = await _session();
    await tester.pumpWidget(_app(session, const HouseholdScreen(householdId: 'h1')));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Convidar'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Adulto'));
    await tester.pumpAndSettle();

    expect(find.text('K7M2QX9P'), findsOneWidget);
    expect(find.byType(QrImageView), findsOneWidget);
    expect(find.text('Copiar código'), findsOneWidget);
  });

  testWidgets('quem chega por um link de convite já encontra o código preenchido', (tester) async {
    final session = await _session();
    session.pendingInvite = 'K7M2QX9P';
    await tester.pumpWidget(_app(session, const OnboardingScreen(canGoBack: true)));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(TextField, 'K7M2QX9P'), findsOneWidget);
    expect(find.textContaining('Ficas a administrar a casa e podes convidar a família.'), findsOneWidget);
  });
}
