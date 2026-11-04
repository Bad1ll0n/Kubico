import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/assistant_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// O servidor responde ao assistente com quatro propostas: artigos, um evento, uma tarefa e uma despesa.
class _Server {
  final requests = <String>[];
  Map<String, dynamic>? asked;

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (request.method != 'GET') requests.add('${request.method} $path');
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
          {'userId': 'rita', 'displayName': 'Rita', 'role': 'Child', 'color': '#A9500B', 'isChildProfile': true},
        ],
      }),
      ('GET', '/api/households/h1/assistant') => _json({
        'available': true,
        'enabled': true,
        'usedToday': 2,
        'dailyLimit': 30,
      }),
      ('POST', '/api/households/h1/assistant') => () {
        asked = jsonDecode(request.body) as Map<String, dynamic>;
        return _json({
          'reply': 'Proponho quatro coisas.',
          'actions': [
            {
              'kind': 'AddItems',
              'listId': 'l1',
              'listName': 'Supermercado',
              'items': ['Leite', 'Pão'],
            },
            {
              'kind': 'CreateEvent',
              'title': 'Dentista',
              'date': '2026-11-06',
              'startsAt': '2026-11-06T18:00:00Z',
              'endsAt': '2026-11-06T19:00:00Z',
            },
            {'kind': 'CreateChore', 'title': 'Regar as plantas'},
            {'kind': 'CreateExpense', 'title': 'Pingo Doce', 'date': '2026-11-04', 'amountCents': 799},
          ],
          'skipped': 1,
          'usedToday': 3,
          'dailyLimit': 30,
        });
      }(),
      ('GET', '/api/households/h1/lists/l1/items') => _json(<Object>[]),
      ('POST', '/api/households/h1/expenses') => () {
        requests.add('expense ${request.body}');
        return _json({}, 201);
      }(),
      ('POST', '/api/households/h1/events') => _json({
        'id': 'e1',
        'householdId': 'h1',
        'title': 'Dentista',
        'isAllDay': false,
        'startsAt': '2026-11-06T18:00:00Z',
        'endsAt': '2026-11-06T19:00:00Z',
        'participantUserIds': <String>[],
        'createdByUserId': 'mae',
        'updatedAt': '2026-11-04T10:00:00Z',
      }, 201),
      ('POST', '/api/households/h1/chores') => _json({
        'id': 'c1',
        'householdId': 'h1',
        'title': 'Regar as plantas',
        'points': 0,
        'recurrence': 'None',
        'createdAt': '2026-11-04T10:00:00Z',
        'updatedAt': '2026-11-04T10:00:00Z',
      }, 201),
      _ => http.Response('', 404),
    };
  });
}

void main() {
  testWidgets('o assistente propõe, a pessoa escolhe e só as escolhidas acontecem (F4-24)', (tester) async {
    tester.view.physicalSize = const Size(700, 2400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final server = _Server();
    final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
    final session = SessionController(api: api, store: MemoryTokenStore());
    await session.refresh();
    final shopping = ShoppingRepository(api: api, store: MemoryKeyValueStore(), saveDelay: Duration.zero);
    final receipt = Uint8List.fromList([0xFF, 0xD8, 0xFF, 1, 2, 3]);

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
          ChangeNotifierProvider.value(value: shopping),
        ],
        child: MaterialApp(
          home: AssistantScreen(
            household: const HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.admin),
            pickFile: (_, {required pdf}) async => (bytes: receipt, name: 'talao.jpg', mediaType: 'image/jpeg'),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('2 de 30 pedidos hoje nesta casa'), findsOneWidget);

    await tester.enterText(find.byType(TextField), 'Põe leite e pão e marca o dentista');
    await tester.tap(find.text('Fotografia de um talão'));
    await tester.pumpAndSettle();
    expect(find.text('talao.jpg'), findsOneWidget);
    await tester.tap(find.text('Pedir propostas'));
    await tester.pumpAndSettle();

    expect(server.asked!['text'], 'Põe leite e pão e marca o dentista');
    expect(base64Decode(server.asked!['fileBase64'] as String), receipt);
    expect(find.text('Proponho quatro coisas.'), findsOneWidget);
    expect(find.text('Pôr na lista «Supermercado»'), findsOneWidget);
    expect(find.text('Leite, Pão'), findsOneWidget);
    expect(find.text('Despesa de 7,99 €, dividida pelos adultos'), findsOneWidget);
    expect(find.text('3 de 30 pedidos hoje nesta casa'), findsOneWidget);

    // A tarefa fica de fora: só as escolhidas se fazem.
    await tester.tap(find.text('Regar as plantas'));
    await tester.pump();
    await tester.tap(find.text('Fazer as escolhidas'));
    await tester.pumpAndSettle();

    expect(server.requests, contains('POST /api/households/h1/events'));
    expect(server.requests, isNot(contains('POST /api/households/h1/chores')));
    final expense = server.requests.firstWhere((r) => r.startsWith('expense '));
    expect(expense, contains('"amountCents":799'));
    expect(expense, contains('"paidByUserId":"mae"'));
    expect(expense, contains('"splitWith":["mae","pai"]'));
    // Os artigos seguem pela fila das compras (com o id gerado na app).
    expect(server.requests.where((r) => r == 'POST /api/households/h1/lists/l1/items'), hasLength(2));
    expect(find.text('Feito.'), findsWidgets);
    shopping.dispose();
  });

  test('cada proposta tem um resumo legível', () {
    final allDay = AssistantAction(
      kind: AssistantActionKind.createEvent,
      title: 'Férias do Natal',
      date: DateTime(2026, 12, 21),
      endDate: DateTime(2027, 1, 2),
    );
    final check = AssistantAction.fromJson({
      'kind': 'CheckItems',
      'listId': 'l1',
      'listName': 'Mercearia',
      'items': ['Leite'],
      'itemIds': ['i1'],
    });

    expect(describeAssistantAction(allDay).$3, '21/12/2026 – 02/01/2027');
    expect(check.kind, AssistantActionKind.checkItems);
    expect(describeAssistantAction(check).$2, 'Marcar como comprado em «Mercearia»');
  });
}
