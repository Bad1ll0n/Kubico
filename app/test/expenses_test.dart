import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/expenses_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// A Mãe pagou 90 € de supermercado entre os três; o Pai e o Rui devem-lhe 30 € cada.
class _Server {
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (request.method != 'GET') requests.add('${request.method} $path ${request.body}');
    return switch ((request.method, path)) {
      ('GET', '/api/me') => _json({
        'user': {'id': 'mae', 'email': 'mae@kubico.test', 'displayName': 'Mãe', 'isChildProfile': false},
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
        ],
      }),
      ('GET', '/api/households/h1') => _json({
        'id': 'h1',
        'name': 'Casa',
        'timeZoneId': 'Europe/Lisbon',
        'myRole': 'Admin',
        'members': [
          {'userId': 'mae', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#2F64A8', 'isChildProfile': false},
          {'userId': 'pai', 'displayName': 'Pai', 'role': 'Adult', 'color': '#2E7550', 'isChildProfile': false},
          {'userId': 'rui', 'displayName': 'Rui', 'role': 'Guest', 'color': '#A9500B', 'isChildProfile': false},
        ],
      }),
      ('GET', '/api/households/h1/expenses') => _json({
        'expenses': [
          {
            'id': 'e1',
            'description': 'Supermercado',
            'amountCents': 9000,
            'paidByUserId': 'mae',
            'date': '2026-11-10',
            'category': null,
            'shares': [
              {'userId': 'mae', 'amountCents': 3000},
              {'userId': 'pai', 'amountCents': 3000},
              {'userId': 'rui', 'amountCents': 3000},
            ],
            'createdByUserId': 'mae',
            'updatedAt': '2026-11-10T10:00:00Z',
          },
        ],
        'settlements': <Object>[],
        'balances': [
          {'userId': 'mae', 'netCents': 6000},
          {'userId': 'pai', 'netCents': -3000},
          {'userId': 'rui', 'netCents': -3000},
        ],
        'transfers': [
          {'fromUserId': 'pai', 'toUserId': 'mae', 'amountCents': 3000},
          {'fromUserId': 'rui', 'toUserId': 'mae', 'amountCents': 3000},
        ],
      }),
      ('POST', _) => _json({}, 201),
      _ => http.Response('', 404),
    };
  });
}

void main() {
  test('os euros leem-se com vírgula ou ponto', () {
    expect(formatEuros(123450), '1234,50 €');
    expect(parseEuros('12,5'), 1250);
    expect(parseEuros('12.50 €'), 1250);
    expect(parseEuros('abc'), isNull);
  });

  testWidgets('os saldos dizem quem deve a quem, acerta-se com um toque e regista-se uma despesa (F4-10)', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(600, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final server = _Server();
    final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
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
        child: const MaterialApp(
          home: Scaffold(
            body: ExpensesView(
              household: HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.admin),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('recebe 60,00 €'), findsOneWidget);
    expect(find.text('deve 30,00 €'), findsNWidgets(2));
    expect(find.text('Pai → Mãe'), findsOneWidget);

    await tester.tap(find.text('Acertar').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Registar'));
    await tester.pumpAndSettle();
    expect(
      server.requests.last,
      startsWith('POST /api/households/h1/settlements {"fromUserId":"pai","toUserId":"mae","amountCents":3000'),
    );

    await tester.tap(find.text('Nova despesa'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'O quê?'), 'Gás');
    await tester.enterText(find.widgetWithText(TextField, 'Valor (€)'), '24,90');
    await tester.tap(find.widgetWithText(FilterChip, 'Rui'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    final body = jsonDecode(server.requests.last.split(' ').skip(2).join(' ')) as Map<String, dynamic>;
    expect(body['description'], 'Gás');
    expect(body['amountCents'], 2490);
    expect(body['paidByUserId'], 'mae');
    expect(body['splitWith'], ['mae', 'pai'], reason: 'o Rui saiu da divisão');
  });
}
