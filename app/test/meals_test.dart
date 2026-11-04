import 'dart:convert';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/meals_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Uma casa com duas receitas; na terça (10/11) ao jantar há sopa.
class _Server {
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    requests.add(
      '${request.method} ${request.url.path}${request.url.hasQuery ? '?${request.url.query}' : ''} ${request.body}',
    );
    return switch ((request.method, path)) {
      ('GET', '/api/households/h1/recipes') => _json([
        {
          'id': 'r1',
          'title': 'Sopa de legumes',
          'servings': 4,
          'ingredients': [
            {'name': 'Cenoura', 'quantity': '3', 'category': 'Frutas e legumes'},
          ],
          'steps': <String>[],
          'tags': ['Sopa'],
        },
        {
          'id': 'r2',
          'title': 'Bacalhau à Brás',
          'servings': 4,
          'ingredients': <Object>[],
          'steps': <String>[],
          'tags': ['Peixe'],
        },
      ]),
      ('GET', '/api/households/h1/meals') => _json([
        {'date': '2026-11-10', 'slot': 'Dinner', 'recipeId': 'r1', 'text': null, 'updatedAt': '2026-11-01T10:00:00Z'},
      ]),
      ('PUT', final p) when p.startsWith('/api/households/h1/meals/') => _json({
        'date': '2026-11-09',
        'slot': 'Lunch',
        'recipeId': (jsonDecode(request.body) as Map)['recipeId'],
        'text': null,
        'updatedAt': '2026-11-01T10:00:00Z',
      }),
      ('GET', '/api/households/h1/lists') => _json([
        {'id': 'l1', 'householdId': 'h1', 'name': 'Supermercado', 'itemCount': 0, 'checkedCount': 0},
      ]),
      ('POST', '/api/households/h1/meals/to-list') => _json({
        'added': 1,
        'names': ['Cenoura'],
      }),
      _ => http.Response('', 404),
    };
  });
}

void main() {
  testWidgets('o plano da semana mostra as receitas, planeia-se um dia e os ingredientes vão para a lista', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(600, 2200);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final server = _Server();
    final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';

    await withClock(Clock.fixed(DateTime(2026, 11, 10, 12)), () async {
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            Provider.value(value: api),
            Provider.value(
              value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
            ),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: MealsView(
                household: HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.admin),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(server.requests, contains('GET /api/households/h1/meals?from=2026-11-09&to=2026-11-15 '));
      expect(find.text('Sopa de legumes'), findsOneWidget);

      // Segunda ao almoço: Bacalhau à Brás.
      await tester.tap(find.text('—').first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Bacalhau à Brás'));
      await tester.pumpAndSettle();
      expect(server.requests.last, 'PUT /api/households/h1/meals/2026-11-09/Lunch {"recipeId":"r2"}');
      expect(find.text('Bacalhau à Brás'), findsOneWidget);

      await tester.tap(find.text('Pôr os ingredientes na lista'));
      await tester.pumpAndSettle();
      expect(
        server.requests.last,
        'POST /api/households/h1/meals/to-list {"from":"2026-11-09","to":"2026-11-15","listId":"l1"}',
      );
      expect(find.text('Foram para a lista «Supermercado»: Cenoura.'), findsOneWidget);
    });
  });
}
