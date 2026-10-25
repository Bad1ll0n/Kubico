import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/shopping_items_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Servidor com uma lista ("l1") que se pode desligar, como a rede no supermercado.
class _Server {
  var online = true;

  /// Com um estado, o servidor responde só isso (base de dados em baixo, proxy, limites…).
  int? failWith;
  final items = <String, Map<String, dynamic>>{'bread': _item('bread', 'Pão')};
  final requests = <String>[];

  static Map<String, dynamic> _item(String id, String name, {bool checked = false}) => {
    'id': id,
    'listId': 'l1',
    'name': name,
    'quantity': null,
    'category': null,
    'isChecked': checked,
    'addedByUserId': 'u1',
    'checkedByUserId': null,
    'createdAt': '2026-10-15T10:00:00Z',
    'updatedAt': '2026-10-15T10:00:00Z',
  };

  late final client = MockClient((request) async {
    if (!online) throw http.ClientException('Sem rede');
    if (failWith case final status?) return _json({'title': 'Falhou ($status)'}, status);
    final path = request.url.path;
    const base = '/api/households/h1/lists/l1/items';
    if (request.method != 'GET') requests.add('${request.method} ${path.replaceFirst(base, '')}');
    if (path == base && request.method == 'GET') return _json(items.values.toList());
    if (path == base && request.method == 'POST') {
      final body = jsonDecode(request.body) as Map<String, dynamic>;
      final id = body['id'] as String;
      final existed = items.containsKey(id);
      items[id] ??= _item(id, body['name'] as String);
      return _json(items[id]!, existed ? 200 : 201);
    }
    final itemId = path.replaceFirst('$base/', '');
    if (request.method == 'PATCH') {
      final item = items[itemId];
      if (item == null) return http.Response('', 404);
      item['isChecked'] = (jsonDecode(request.body) as Map<String, dynamic>)['isChecked'];
      return _json(item);
    }
    if (request.method == 'DELETE') {
      return items.remove(itemId) == null ? http.Response('', 404) : http.Response('', 204);
    }
    return http.Response('', 404);
  });
}

ShoppingRepository _repository(_Server server, KeyValueStore store, {String? token = 't'}) => ShoppingRepository(
  api: ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = token,
  store: store,
  retryEvery: const Duration(hours: 1),
);

List<String> _names(List<ShoppingItem> items) => [for (final i in items) i.name]..sort();

void main() {
  test('sem rede, os artigos novos e marcados aparecem logo e são enviados quando a rede volta', () async {
    final server = _Server();
    final shopping = _repository(server, MemoryKeyValueStore());
    expect(_names(await shopping.items('h1', 'l1')), ['Pão']);

    server.online = false;
    final milk = shopping.addItem('h1', 'l1', name: 'Leite', quantity: '2');
    shopping.setChecked('h1', 'l1', 'bread', true);
    await shopping.sync();

    expect(shopping.isOffline, isTrue);
    expect(shopping.pendingCount, 2);
    final offline = await shopping.items('h1', 'l1');
    expect(_names(offline), ['Leite', 'Pão']);
    expect(offline.firstWhere((i) => i.name == 'Pão').isChecked, isTrue);

    server.online = true;
    await shopping.sync();

    expect(shopping.isOffline, isFalse);
    expect(shopping.pendingCount, 0);
    expect(server.requests, ['POST ', 'PATCH /bread']);
    expect(server.items.keys, contains(milk.id), reason: 'o artigo fica com o id gerado no telemóvel');
  });

  test('a fila e a cópia da lista sobrevivem a fechar a app', () async {
    final server = _Server();
    final store = MemoryKeyValueStore();
    final before = _repository(server, store);
    await before.items('h1', 'l1');
    server.online = false;
    before.addItem('h1', 'l1', name: 'Café');
    await before.sync();

    final after = _repository(server, store);
    await after.restore();

    expect(after.pendingCount, 1);
    expect(_names(await after.items('h1', 'l1')), ['Café', 'Pão']);
  });

  test('reenviar não duplica, e apagar um artigo que já não existe não fica preso na fila', () async {
    final server = _Server();
    final shopping = _repository(server, MemoryKeyValueStore());
    final tea = shopping.addItem('h1', 'l1', name: 'Chá');
    await shopping.sync();

    // Como se a app tivesse fechado antes de saber que o pedido chegou: volta a enviar o mesmo artigo.
    await shopping.api.addItem('h1', 'l1', id: tea.id, name: 'Chá');
    server.items.remove('bread');
    shopping.deleteItem('h1', 'l1', 'bread');
    await shopping.sync();

    expect(server.items.values.where((i) => i['name'] == 'Chá'), hasLength(1));
    expect(shopping.pendingCount, 0);
  });

  for (final status in [401, 408, 429, 500, 502, 503]) {
    test('com o servidor a responder $status, a alteração fica na fila e chega quando ele recupera', () async {
      final server = _Server();
      final shopping = _repository(server, MemoryKeyValueStore());
      final rejected = <ShoppingRejection>[];
      shopping.rejections.listen(rejected.add);
      await shopping.items('h1', 'l1');

      server.failWith = status;
      final milk = shopping.addItem('h1', 'l1', name: 'Leite');
      await shopping.sync();
      expect(shopping.pendingCount, 1);

      server.failWith = null;
      await shopping.sync();
      expect(shopping.pendingCount, 0);
      expect(server.items.keys, contains(milk.id));
      expect(rejected, isEmpty);
    });
  }

  test('uma alteração recusada de vez (400) sai da fila e é anunciada, sem prender as seguintes', () async {
    final server = _Server();
    final shopping = _repository(server, MemoryKeyValueStore());
    final rejected = <ShoppingRejection>[];
    shopping.rejections.listen(rejected.add);
    await shopping.items('h1', 'l1');

    server.failWith = 400;
    shopping.addItem('h1', 'l1', name: 'Leite');
    await shopping.sync();
    await Future<void>.delayed(Duration.zero);

    expect(shopping.pendingCount, 0);
    expect(rejected.single.change.name, 'Leite');
    expect(rejected.single.message, 'Falhou (400)');
  });

  test('um erro do servidor que não passa acaba por soltar a fila', () async {
    final server = _Server()..failWith = 500;
    final shopping = _repository(server, MemoryKeyValueStore());
    final rejected = <ShoppingRejection>[];
    shopping.rejections.listen(rejected.add);
    shopping.addItem('h1', 'l1', name: 'Leite');
    for (var i = 0; i < ShoppingRepository.maxServerFailures; i++) {
      await shopping.sync();
    }
    await Future<void>.delayed(Duration.zero);

    expect(shopping.pendingCount, 0);
    expect(rejected, hasLength(1));
  });

  test('ao abrir a app, a fila espera pela sessão em vez de a enviar sem token', () async {
    final server = _Server();
    final store = MemoryKeyValueStore();
    final before = _repository(server, store);
    server.online = false;
    final coffee = before.addItem('h1', 'l1', name: 'Café');
    await before.sync();
    server.online = true;

    final after = _repository(server, store, token: null);
    await after.restore();
    await after.sync();
    expect(server.requests, isEmpty, reason: 'sem token não se envia nada');
    expect(after.pendingCount, 1);

    after.api.accessToken = 't';
    await after.sync();
    expect(after.pendingCount, 0);
    expect(server.items.keys, contains(coffee.id));
  });

  test('ids gerados no telemóvel são UUIDs v4 diferentes', () {
    final ids = {for (var i = 0; i < 100; i++) newItemId()};

    expect(ids, hasLength(100));
    expect(ids.first, matches(RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')));
  });

  testWidgets('a lista avisa quando não há rede e continua a aceitar artigos', (tester) async {
    final server = _Server();
    final shopping = _repository(server, MemoryKeyValueStore());
    await shopping.items('h1', 'l1');
    server.online = false;
    const list = ShoppingList(id: 'l1', householdId: 'h1', name: 'Supermercado', itemCount: 1, checkedCount: 0);

    await tester.pumpWidget(
      MultiProvider(
        providers: [
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

    expect(find.text('Pão'), findsOneWidget);
    await tester.enterText(find.byType(TextField), '2 leite');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();

    expect(find.text('leite'), findsOneWidget);
    expect(find.textContaining('Sem ligação'), findsOneWidget);
    expect(find.textContaining('1 alteração por enviar'), findsOneWidget);

    // Sai do ecrã e pára a fila (que, sem rede, ficou a tentar de hora a hora).
    await tester.pumpWidget(const SizedBox());
    shopping.dispose();
  });
}
