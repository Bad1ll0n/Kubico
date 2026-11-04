import 'dart:convert';

import 'package:flutter/gestures.dart';
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

Map<String, dynamic> _item(String id, String name, int second, {int? position}) => {
  'id': id,
  'listId': 'l1',
  'name': name,
  'quantity': null,
  'category': null,
  'isChecked': false,
  'addedByUserId': 'u1',
  'createdAt': '2026-10-19T10:00:${second.toString().padLeft(2, '0')}Z',
  'updatedAt': '2026-10-19T10:00:00Z',
  'position': ?position,
};

void main() {
  setUp(() async => ShoppingOrder.restore(MemoryKeyValueStore()));

  testWidgets('arrastar um artigo muda a ordem para toda a casa e a ordem guardada vence a de chegada', (tester) async {
    tester.view.physicalSize = const Size(600, 1400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final sent = <String>[];
    final client = MockClient((request) async {
      if (request.method != 'GET') sent.add('${request.method} ${request.url.path} ${request.body}');
      return switch ((request.method, request.url.path)) {
        // O Queijo foi arrumado à mão para o topo; os outros vão pela ordem em que se puseram.
        ('GET', '/api/households/h1/lists/l1/items') => _json([
          _item('leite', 'Leite', 1),
          _item('pao', 'Pão', 2),
          _item('queijo', 'Queijo', 3, position: 0),
        ]),
        ('GET', '/api/households/h1/lists/l1/insights') => _json({
          'suggestions': <Object>[],
          'categoryOrder': <Object>[],
        }),
        ('PUT', '/api/households/h1/lists/l1/items/order') => http.Response('', 204),
        _ => http.Response('', 404),
      };
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
    final shopping = ShoppingRepository(
      api: api,
      store: MemoryKeyValueStore(),
      retryEvery: const Duration(hours: 1),
      saveDelay: Duration.zero,
    );
    addTearDown(shopping.dispose);
    const list = ShoppingList(id: 'l1', householdId: 'h1', name: 'Super', itemCount: 3, checkedCount: 0);
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

    double top(String text) => tester.getTopLeft(find.text(text)).dy;
    expect(top('Queijo'), lessThan(top('Leite')));
    expect(top('Leite'), lessThan(top('Pão')));

    // Carregar sem largar no Pão e arrastá-lo para cima do Queijo.
    final gesture = await tester.startGesture(tester.getCenter(find.text('Pão')), kind: PointerDeviceKind.touch);
    await tester.pump(kLongPressTimeout + const Duration(milliseconds: 100));
    final distance = top('Queijo') - top('Pão') - 30;
    for (var step = 0; step < 10; step++) {
      await gesture.moveBy(Offset(0, distance / 10));
      await tester.pump(const Duration(milliseconds: 20));
    }
    await gesture.up();
    await tester.pumpAndSettle();

    expect(top('Pão'), lessThan(top('Queijo')));
    expect(sent, contains('PUT /api/households/h1/lists/l1/items/order {"ids":["pao","queijo","leite"]}'));
  });

  test('uma ordem feita sem rede fica na fila e aparece logo na cópia da lista', () async {
    var online = false;
    final orders = <String>[];
    final client = MockClient((request) async {
      if (!online) throw http.ClientException('sem rede');
      if (request.method == 'PUT') orders.add(request.body);
      return request.method == 'GET' ? _json([_item('a', 'A', 1), _item('b', 'B', 2)]) : http.Response('', 204);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
    final store = MemoryKeyValueStore();
    final shopping = ShoppingRepository(
      api: api,
      store: store,
      retryEvery: const Duration(hours: 1),
      saveDelay: Duration.zero,
    );

    online = true;
    await shopping.items('h1', 'l1');
    online = false;
    shopping.reorder('h1', 'l1', ['b', 'a']);
    await shopping.sync();
    final offline = await shopping.items('h1', 'l1');

    expect(shopping.pendingCount, 1);
    expect({for (final i in offline) i.id: i.position}, {'b': 0, 'a': 1});

    online = true;
    await shopping.sync();
    expect(orders, ['{"ids":["b","a"]}']);
    expect(shopping.pendingCount, 0);
    shopping.dispose();
  });
}
