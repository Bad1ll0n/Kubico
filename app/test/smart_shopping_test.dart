import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/shopping/shopping_order.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/shopping_items_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _item(String id, String name, String category) => {
  'id': id,
  'listId': 'l1',
  'name': name,
  'quantity': null,
  'category': category,
  'isChecked': false,
  'addedByUserId': 'u1',
  'createdAt': '2026-10-19T10:00:0${id.length}Z',
  'updatedAt': '2026-10-19T10:00:00Z',
};

/// Uma lista com pão (Padaria) e maçãs (Fruta); no supermercado desta casa costuma-se apanhar o pão primeiro.
class _Server {
  final posts = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (request.method != 'GET') posts.add('${request.method} $path ${request.body}');
    return switch ((request.method, path)) {
      ('GET', '/api/households/h1/lists/l1/items') => _json([
        _item('a', 'Pão', 'Padaria'),
        _item('bb', 'Maçãs', 'Fruta'),
      ]),
      ('GET', '/api/households/h1/lists/l1/insights') => _json({
        'suggestions': [
          {'name': 'Leite', 'category': 'Laticínios', 'lastBought': '2026-10-12', 'everyDays': 7},
        ],
        'categoryOrder': ['Padaria', 'Fruta'],
      }),
      ('POST', '/api/households/h1/lists/l1/items') => _json({
        ..._item('c', 'Leite', 'Laticínios'),
        'id': (jsonDecode(request.body) as Map)['id'],
      }, 201),
      ('POST', '/api/households/h1/lists/l1/recurring') => _json({
        'id': 'r1',
        'listId': 'l1',
        'name': 'Pão',
        'quantity': null,
        'category': 'Padaria',
        'intervalDays': 7,
        'nextDate': '2026-10-26',
      }, 201),
      _ => http.Response('', 404),
    };
  });
}

Future<void> _pump(WidgetTester tester, _Server server) async {
  tester.view.physicalSize = const Size(600, 1400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
  final shopping = ShoppingRepository(
    api: api,
    store: MemoryKeyValueStore(),
    retryEvery: const Duration(hours: 1),
    saveDelay: Duration.zero,
  );
  addTearDown(shopping.dispose);
  const list = ShoppingList(id: 'l1', householdId: 'h1', name: 'Supermercado', itemCount: 2, checkedCount: 0);
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: shopping),
        Provider.value(
          value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
        ),
      ],
      child: const MaterialApp(
        home: Scaffold(
          body: ShoppingItemsView(householdId: 'h1', list: list),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  setUp(() async => ShoppingOrder.restore(MemoryKeyValueStore()));

  testWidgets('as categorias seguem a ordem do supermercado, e pode voltar-se à ordem alfabética (F4-22c)', (
    tester,
  ) async {
    await _pump(tester, _Server());

    double top(String text) => tester.getTopLeft(find.text(text)).dy;
    expect(top('Pão'), lessThan(top('Maçãs')), reason: 'o pão costuma apanhar-se antes da fruta');

    await tester.tap(find.byTooltip('Mais opções da lista'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Pela ordem em que costumo comprar'));
    await tester.pumpAndSettle();

    expect(ShoppingOrder.learned.value, isFalse);
    expect(top('Maçãs'), lessThan(top('Pão')), reason: 'Fruta antes de Padaria, por ordem alfabética');
  });

  testWidgets('"Costumas comprar" põe o artigo na lista com um toque, e "Repetir" fá-lo voltar sozinho', (
    tester,
  ) async {
    final server = _Server();
    await _pump(tester, server);

    expect(find.text('Costumas comprar'), findsOneWidget);
    await tester.tap(find.widgetWithText(ActionChip, 'Leite'));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(ActionChip, 'Leite'), findsNothing);
    expect(server.posts.any((p) => p.contains('"name":"Leite"')), isTrue);

    await tester.tap(find.byTooltip('Repetir «Pão»'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Todas as semanas'));
    await tester.pumpAndSettle();
    expect(
      server.posts.last,
      'POST /api/households/h1/lists/l1/recurring {"name":"Pão","intervalDays":7,"category":"Padaria"}',
    );
    expect(find.text('«Pão» volta à lista sozinho.'), findsOneWidget);
  });

  testWidgets('deslizar um artigo para a direita marca-o como comprado (F4-04)', (tester) async {
    final server = _Server();
    await _pump(tester, server);

    await tester.drag(find.text('Pão'), const Offset(400, 0));
    await tester.pumpAndSettle();

    expect(
      server.posts.any(
        (p) => p.startsWith('PATCH /api/households/h1/lists/l1/items/a') && p.contains('"isChecked":true'),
      ),
      isTrue,
    );
    expect(find.text('Pão'), findsOneWidget, reason: 'o artigo fica na lista, agora no carrinho');
  });
}
