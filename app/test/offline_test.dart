import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/offline/chore_queue.dart';
import 'package:kubico/src/offline/response_cache.dart';
import 'package:kubico/src/storage/key_value_store.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _chore(String id) => {
  'id': id,
  'householdId': 'h1',
  'title': 'Pôr a mesa',
  'notes': null,
  'points': 5,
  'dueDate': '2026-11-10',
  'recurrence': 'None',
  'assigneeUserId': 'rita',
  'rotationUserIds': <String>[],
  'isDone': true,
  'updatedAt': '2026-11-10T10:00:00Z',
};

void main() {
  group('cópia sem rede (F4-14)', () {
    test('sem ligação, uma leitura da casa abre com a última cópia e avisa; a sessão e as compras não', () async {
      var online = true;
      final api =
          ApiClient(
              baseUrl: 'http://api',
              httpClient: MockClient((request) async {
                if (!online) throw http.ClientException('sem rede');
                return _json([_chore('c1')]);
              }),
            )
            ..accessToken = 't'
            ..cache = ResponseCache(store: MemoryKeyValueStore(), saveDelay: Duration.zero);

      expect(await api.getChores('h1'), hasLength(1));
      expect(api.showingCopy.value, isFalse);

      online = false;
      final copy = await api.getChores('h1');
      expect(copy.single.title, 'Pôr a mesa');
      expect(api.showingCopy.value, isTrue, reason: 'o ecrã avisa que é uma cópia');

      await expectLater(api.getLists('h1'), throwsA(isA<ApiException>()), reason: 'as compras têm a sua fila');

      online = true;
      await api.getChores('h1');
      expect(api.showingCopy.value, isFalse);
    });

    test('a cópia grava-se no aparelho, volta ao arrancar e apaga-se ao sair da sessão', () async {
      final store = MemoryKeyValueStore();
      final cache = ResponseCache(store: store, saveDelay: Duration.zero)..write('/api/households/h1', '{"id":"h1"}');
      await cache.flush();

      final restored = ResponseCache(store: store);
      await restored.restore();
      expect(restored.read('/api/households/h1'), '{"id":"h1"}');

      await restored.clear();
      expect(restored.read('/api/households/h1'), isNull);
      expect(await store.read('kubico.offline.responses'), isNull);
    });

    test('guarda no máximo as respostas mais recentes', () {
      final cache = ResponseCache(store: MemoryKeyValueStore(), maxEntries: 2)
        ..write('/api/households/a', '1')
        ..write('/api/households/b', '2');
      cache.read('/api/households/a'); // usada agora: a mais antiga passa a ser a b
      cache.write('/api/households/c', '3');

      expect(cache.read('/api/households/b'), isNull);
      expect(cache.read('/api/households/a'), '1');
      expect(cache.read('/api/households/c'), '3');
    });
  });

  group('fila das tarefas sem rede (F4-14)', () {
    test('a conclusão fica guardada, vai quando há rede e um 409 sai da fila com aviso', () async {
      var status = 0;
      final sent = <String>[];
      final api = ApiClient(
        baseUrl: 'http://api',
        httpClient: MockClient((request) async {
          if (status == 0) throw http.ClientException('sem rede');
          sent.add('${request.url.path} ${request.body}');
          if (status == 409) return _json({'title': 'Esta tarefa já está feita.', 'status': 409}, 409);
          return _json({'chore': _chore('c1'), 'pointsEarned': 5});
        }),
      )..accessToken = 't';
      final store = MemoryKeyValueStore();
      final queue = ChoreQueue(api: api, store: store);
      final rejected = <CompletionRejection>[];
      queue.rejections.listen(rejected.add);

      await queue.enqueue(householdId: 'h1', choreId: 'c1', title: 'Pôr a mesa', doneByUserId: 'rita');
      await queue.enqueue(householdId: 'h1', choreId: 'c2', title: 'Lixo');
      expect(queue.pendingIn('h1'), {'c1', 'c2'});

      // Sem rede: fica tudo, e sobrevive a fechar a app.
      await queue.sync();
      final reopened = ChoreQueue(api: api, store: store);
      await reopened.restore();
      expect(reopened.pendingIn('h1'), {'c1', 'c2'});

      status = 200;
      await reopened.sync();
      expect(sent, [
        '/api/households/h1/chores/c1/complete {"doneByUserId":"rita"}',
        '/api/households/h1/chores/c2/complete ',
      ]);
      expect(reopened.pending, isEmpty);

      status = 409;
      await queue.clear();
      await queue.enqueue(householdId: 'h1', choreId: 'c3', title: 'Aspirar');
      await queue.sync();
      await Future<void>.delayed(Duration.zero);
      expect(queue.pending, isEmpty);
      expect(rejected.single.completion.title, 'Aspirar');
      expect(rejected.single.message, 'Esta tarefa já está feita.');
    });

    test('sem sessão não envia nada', () async {
      final api = ApiClient(baseUrl: 'http://api', httpClient: MockClient((_) async => fail('não devia enviar')));
      final queue = ChoreQueue(api: api, store: MemoryKeyValueStore());
      await queue.enqueue(householdId: 'h1', choreId: 'c1', title: 'Lixo');

      await queue.sync();

      expect(queue.pending, hasLength(1));
    });
  });
}
