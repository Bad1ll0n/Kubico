import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/onboarding_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

void main() {
  testWidgets('ao criar a casa escolhe-se um modelo, que vai no pedido', (tester) async {
    tester.view.physicalSize = const Size(900, 1800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    final created = <Map<String, dynamic>>[];
    final client = MockClient((request) async {
      switch (request.url.path) {
        case '/api/me':
          return _json({
            'user': {'id': 'u1', 'email': 'mae@kubico.test', 'displayName': 'Mãe'},
            'households': [
              for (final _ in created) {'id': 'h1', 'name': 'Casa da Amora', 'role': 'Admin'},
            ],
          });
        case '/api/household-templates':
          return _json([
            {
              'key': 'FamilyWithKids',
              'name': 'Família com crianças',
              'description': 'Rotinas da manhã e da noite para os mais novos.',
              'lists': ['Supermercado', 'Farmácia'],
              'chores': ['Rotina da manhã', 'Rotina da noite', 'Pôr a mesa'],
              'rewards': ['Escolher o filme de sexta'],
            },
            {
              'key': 'Blank',
              'name': 'Começar do zero',
              'description': 'Uma casa vazia.',
              'lists': <String>[],
              'chores': <String>[],
              'rewards': <String>[],
            },
          ]);
        case '/api/households':
          created.add(jsonDecode(request.body) as Map<String, dynamic>);
          return _json({'id': 'h1', 'name': 'Casa da Amora', 'role': 'Admin'}, 201);
      }
      return http.Response('', 404);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
    final session = SessionController(api: api, store: MemoryTokenStore());
    await session.refresh();

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
          ),
        ],
        child: const MaterialApp(home: OnboardingScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Família com crianças'), findsOneWidget);
    expect(find.textContaining('2 listas · 3 tarefas · 1 recompensa'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextField, 'Nome'), 'Casa da Amora');
    await tester.tap(find.text('Família com crianças'));
    await tester.pump();
    await tester.tap(find.text('Criar casa'));
    await tester.pumpAndSettle();

    expect(created.single, {'name': 'Casa da Amora', 'template': 'FamilyWithKids'});
    expect(session.households.single.name, 'Casa da Amora');
  });
}
