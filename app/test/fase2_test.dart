import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/l10n/flutter_localizations_pt_en.dart';
import 'package:kubico/src/notifications/push_registration.dart';
import 'package:kubico/src/realtime/coalesced_reload.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/calendar_view.dart';
import 'package:kubico/src/ui/chore_history_screen.dart';
import 'package:kubico/src/ui/rewards_view.dart';
import 'package:kubico/src/ui/shopping_items_view.dart';
import 'package:kubico/src/ui/shopping_lists_view.dart';
import 'package:provider/provider.dart';

/// Fase 2 (desempenho): a app deixa de recarregar tudo a cada evento do tempo real (P2, P4), o calendário indexa as
/// ocorrências por dia (P5), as listas grandes só constroem o que se vê (P10), a cópia sem rede grava-se agrupada
/// (P11), as miniaturas descodificam-se pequenas (P12) e a web só traz pt e en e só arranca o Firebase ao entrar (P8).

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Uma API falsa que conta os pedidos por caminho e responde com [routes].
class _Api {
  _Api(this.routes);

  final Map<String, Object Function()> routes;
  final calls = <String>[];

  int count(String path) => calls.where((c) => c == path).length;

  late final client = MockClient((request) async {
    final path = request.url.path;
    calls.add(path);
    final route = routes[path];
    return route == null ? http.Response('', 404) : _json(route());
  });

  ApiClient get api => ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
}

HouseholdRealtime _realtime() => HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't');

Map<String, dynamic> _item(String id, String name, {bool checked = false}) => {
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

void main() {
  group('P2 — recargas juntas', () {
    testWidgets('trinta pedidos seguidos dão uma recarga; um pedido durante a recarga dá mais uma no fim', (
      tester,
    ) async {
      var loads = 0;
      final reload = CoalescedReload(() async {
        loads++;
        await Future<void>.delayed(const Duration(milliseconds: 100));
      });

      for (var i = 0; i < 30; i++) {
        reload.request();
        await tester.pump(const Duration(milliseconds: 10));
      }
      await tester.pump(const Duration(milliseconds: 300));
      expect(loads, 1);

      reload.request(); // durante a recarga
      await tester.pump(const Duration(seconds: 1));
      expect(loads, 2);
      reload.dispose();
    });

    testWidgets('o calendário junta os eventos que chegam numa só recarga do intervalo, sem voltar a pedir a casa', (
      tester,
    ) async {
      var version = 0;
      final fake = _Api({
        '/api/households/h1': () => {
          'id': 'h1',
          'name': 'Casa',
          'timeZoneId': 'Europe/Lisbon',
          'myRole': 'Adult',
          'members': [],
        },
        '/api/households/h1/events': () => {'events': [], 'occurrences': [], 'holidays': []},
      });
      final realtime = _realtime();
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            Provider.value(value: fake.api),
            Provider.value(value: realtime),
          ],
          child: MaterialApp(
            home: CalendarView(
              household: const HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.adult),
              initialDate: DateTime(2026, 11, 10),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect((fake.count('/api/households/h1/events'), fake.count('/api/households/h1')), (1, 1));

      // Cinco eventos criados por outra pessoa, seguidos (antes: 5 × o mês + 5 × a casa).
      for (var i = 0; i < 5; i++) {
        realtime.emitForTest(
          CalendarEventUpserted(CalendarEvent.fromJson(_event('n$i', updatedAt: '2026-10-20T10:0${version++}:00Z'))),
        );
        await tester.pump(const Duration(milliseconds: 20));
      }
      realtime.emitForTest(const CalendarEventsDeleted(['n0', 'n1']));
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pumpAndSettle();

      expect(fake.count('/api/households/h1/events'), 2);
      expect(fake.count('/api/households/h1'), 1, reason: 'os membros têm o seu evento (MembersChanged)');
    });

    testWidgets('as listas atualizam as contagens com o ShoppingListUpserted, sem pedir as listas a cada artigo', (
      tester,
    ) async {
      final fake = _Api({
        '/api/households/h1/lists': () => [
          {'id': 'l1', 'householdId': 'h1', 'name': 'Supermercado', 'itemCount': 3, 'checkedCount': 0},
        ],
      });
      final realtime = _realtime();
      final shopping = ShoppingRepository(api: fake.api, store: MemoryKeyValueStore(), saveDelay: Duration.zero);
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            Provider.value(value: fake.api),
            Provider.value(value: realtime),
            ChangeNotifierProvider.value(value: shopping),
          ],
          child: MaterialApp(
            home: ShoppingListsView(
              household: const HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.adult),
              onOpen: (_) {},
              onDeleted: (_) {},
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('3 por comprar'), findsOneWidget);

      for (var i = 0; i < 30; i++) {
        realtime.emitForTest(ShoppingItemUpserted(ShoppingItem.fromJson(_item('i$i', 'Artigo $i', checked: true))));
      }
      realtime.emitForTest(const ShoppingItemsDeleted('l1', ['i1', 'i2']));
      realtime.emitForTest(
        const ShoppingListUpserted(
          ShoppingList(id: 'l1', householdId: 'h1', name: 'Supermercado', itemCount: 3, checkedCount: 2),
        ),
      );
      await tester.pumpAndSettle();

      expect(fake.count('/api/households/h1/lists'), 1, reason: 'só o pedido ao abrir');
      expect(find.text('1 por comprar'), findsOneWidget);
    });

    testWidgets('limpar o carrinho chega numa só mensagem e tira os artigos todos da lista aberta', (tester) async {
      final fake = _Api({
        '/api/households/h1/lists/l1/items': () => [
          _item('a', 'Pão'),
          _item('b', 'Leite', checked: true),
          _item('c', 'Ovos', checked: true),
        ],
      });
      final realtime = _realtime();
      final shopping = ShoppingRepository(api: fake.api, store: MemoryKeyValueStore(), saveDelay: Duration.zero);
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            ChangeNotifierProvider.value(value: shopping),
            Provider.value(value: realtime),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: ShoppingItemsView(
                householdId: 'h1',
                list: ShoppingList(id: 'l1', householdId: 'h1', name: 'Supermercado', itemCount: 3, checkedCount: 2),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('No carrinho (2)'), findsOneWidget);

      realtime.emitForTest(const ShoppingItemsDeleted('l1', ['b', 'c']));
      await tester.pumpAndSettle();

      expect(find.text('Pão'), findsOneWidget);
      expect(find.text('Leite'), findsNothing);
      expect(find.textContaining('No carrinho'), findsNothing);
    });

    testWidgets('as recompensas só se voltam a ler quando mudam os pontos, e uma vez para vários', (tester) async {
      final fake = _Api({
        '/api/me': () => {
          'user': {'id': 'mom', 'email': 'mom@kubico.test', 'displayName': 'Mãe'},
          'households': [
            {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
          ],
        },
        '/api/households/h1/rewards': () => {'rewards': [], 'redemptions': [], 'points': []},
        '/api/households/h1/allowances': () => <Object>[],
      });
      final session = SessionController(api: fake.api, store: MemoryTokenStore());
      await session.refresh();
      final realtime = _realtime();
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            Provider.value(value: fake.api),
            ChangeNotifierProvider.value(value: session),
            Provider.value(value: realtime),
          ],
          child: MaterialApp(home: RewardsView(household: session.currentHousehold!)),
        ),
      );
      await tester.pumpAndSettle();
      final rewardsAtStart = fake.count('/api/households/h1/rewards');
      final allowancesAtStart = fake.count('/api/households/h1/allowances');

      // Mudar o título de uma tarefa não mexe nos pontos.
      realtime.emitForTest(ChoreUpserted(_chore()));
      await tester.pump(const Duration(milliseconds: 400));
      expect(fake.count('/api/households/h1/rewards'), rewardsAtStart);

      // Três tarefas feitas seguidas: uma recarga dos saldos e uma da mesada.
      for (var i = 0; i < 3; i++) {
        realtime.emitForTest(const PointsChanged('h1'));
      }
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pumpAndSettle();
      expect(fake.count('/api/households/h1/rewards'), rewardsAtStart + 1);
      expect(fake.count('/api/households/h1/allowances'), allowancesAtStart + 1);
    });
  });

  group('P5 — o calendário indexado por dia', () {
    test('dá as mesmas ocorrências, conflitos e feriados que filtrar dia a dia', () {
      final random = Random(5);
      for (var round = 0; round < 40; round++) {
        final events = <String, CalendarEvent>{};
        final occurrences = <CalendarOccurrence>[];
        for (var i = 0; i < 60; i++) {
          final id = 'e${random.nextInt(25)}';
          if (random.nextInt(10) > 0) {
            events[id] = CalendarEvent.fromJson(_event(id, updatedAt: '2026-10-01T00:00:00Z'));
          }
          final day = DateTime(2026, 10, 1 + random.nextInt(40));
          final allDay = random.nextInt(4) == 0;
          final start = day.add(Duration(minutes: random.nextInt(24 * 60)));
          final minutes = switch (random.nextInt(6)) {
            0 => 0,
            1 => -60,
            2 => 3 * 24 * 60,
            _ => 30 + random.nextInt(600),
          };
          occurrences.add(
            CalendarOccurrence(
              eventId: id,
              occurrenceDate: day,
              startsAt: allDay ? null : start,
              endsAt: allDay
                  ? null
                  : (random.nextInt(8) == 0
                        ? DateTime(day.year, day.month, day.day + 1)
                        : start.add(Duration(minutes: minutes))),
              startDate: allDay ? day : null,
              endDate: allDay ? day.add(Duration(days: random.nextInt(4) - 1)) : null,
              conflictUserIds: [if (random.nextBool()) 'u${random.nextInt(3)}'],
            ),
          );
        }
        final holidays = [
          Holiday(date: DateTime(2026, 10, 5), name: 'Implantação da República'),
          Holiday(date: DateTime(2026, 11, 1), name: 'Todos os Santos'),
        ];
        final range = CalendarRange(events: events, occurrences: occurrences, holidays: holidays);

        for (
          var d = DateTime(2026, 9, 28);
          d.isBefore(DateTime(2026, 11, 15));
          d = DateTime(d.year, d.month, d.day + 1)
        ) {
          final naive = occurrences.where((o) => o.occursOn(d) && events.containsKey(o.eventId)).toList();
          expect(range.on(d), naive, reason: '$d');
          final holiday = holidays.where(
            (h) => h.date.year == d.year && h.date.month == d.month && h.date.day == d.day,
          );
          expect(range.holidayOn(d), holiday.firstOrNull);
        }
        for (final id in {...events.keys, 'e99'}) {
          final naive = occurrences.where((o) => o.eventId == id).expand((o) => o.conflictUserIds).toSet();
          expect(range.conflictsFor(id), naive);
        }
      }
    });
  });

  group('P11 — cópia sem rede gravada agrupada', () {
    test('várias alterações seguidas gravam a cópia da lista uma vez', () async {
      final items = {'a': _item('a', 'Pão'), 'b': _item('b', 'Leite')};
      final client = MockClient((request) async {
        if (request.method == 'GET') return _json(items.values.toList());
        final id = request.url.pathSegments.last;
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        return _json({...items[id]!, 'isChecked': body['isChecked']});
      });
      final store = _CountingStore();
      final shopping = ShoppingRepository(
        api: ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't',
        store: store,
        saveDelay: const Duration(milliseconds: 50),
      );
      await shopping.items('h1', 'l1');
      final writesAfterLoad = store.writes('kubico.shopping.items.l1');

      for (var i = 0; i < 6; i++) {
        shopping.setChecked('h1', 'l1', i.isEven ? 'a' : 'b', i < 3);
      }
      await shopping.sync();
      expect(store.writes('kubico.shopping.items.l1'), writesAfterLoad, reason: 'ainda só em memória');

      await Future<void>.delayed(const Duration(milliseconds: 120));
      expect(store.writes('kubico.shopping.items.l1'), writesAfterLoad + 1);
      final saved = jsonDecode(store.values['kubico.shopping.items.l1']!) as List;
      expect(saved.firstWhere((i) => i['id'] == 'a')['isChecked'], isFalse);
      shopping.dispose();
    });
  });

  group('P8 — arranque da web', () {
    test('o serviço de avisos (Firebase) só arranca quando a pessoa entra', () async {
      var created = 0;
      final push = PushRegistration.lazy(
        api: ApiClient(baseUrl: 'http://api', httpClient: MockClient((_) async => http.Response('', 204))),
        create: () {
          created++;
          return const NoPushTokenSource();
        },
      );

      await push.unregister();
      expect(created, 0, reason: 'sair sem ter entrado não arranca nada');
      await push.register();
      await push.register();
      expect(created, 1);
    });

    testWidgets('só com pt e en, os textos e as datas do Material continuam em português e em inglês', (tester) async {
      Future<(MaterialLocalizations, MaterialLocalizations)> load() async {
        late MaterialLocalizations pt;
        late MaterialLocalizations en;
        await tester.pumpWidget(
          MaterialApp(
            locale: const Locale('pt', 'PT'),
            supportedLocales: const [Locale('pt', 'PT'), Locale('en', 'GB')],
            localizationsDelegates: kubicoLocalizationsDelegates,
            home: Builder(
              builder: (context) {
                pt = MaterialLocalizations.of(context);
                return Localizations.override(
                  context: context,
                  locale: const Locale('en', 'GB'),
                  child: Builder(
                    builder: (context) {
                      en = MaterialLocalizations.of(context);
                      return const SizedBox();
                    },
                  ),
                );
              },
            ),
          ),
        );
        await tester.pumpAndSettle();
        return (pt, en);
      }

      final (pt, en) = await load();
      expect(pt.cancelButtonLabel, 'Cancelar');
      expect(pt.formatMonthYear(DateTime(2026, 10, 24)), 'outubro de 2026');
      expect(pt.formatFullDate(DateTime(2026, 10, 24)), 'sábado, 24 de outubro de 2026');
      expect(en.cancelButtonLabel, 'Cancel');
      expect(en.formatMonthYear(DateTime(2026, 10, 24)), 'October 2026');
      expect(kubicoLocalizationsDelegates.every((d) => !d.isSupported(const Locale('de'))), isTrue);
    });
  });

  testWidgets('P12 — as miniaturas das fotos descodificam-se no tamanho em que aparecem', (tester) async {
    final client = MockClient((_) async => http.Response.bytes(_png, 200, headers: {'content-type': 'image/png'}));
    tester.view.devicePixelRatio = 2;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      Provider.value(
        value: ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't',
        child: const MaterialApp(
          home: ChorePhotoImage(householdId: 'h1', photoId: 'p-miniatura', size: 48),
        ),
      ),
    );
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
    await tester.pump();

    final image = tester.widget<Image>(find.byType(Image)).image;
    expect(image, isA<ResizeImage>());
    expect((image as ResizeImage).width, 48 * 2 * 2);
  });
}

Map<String, dynamic> _event(String id, {required String updatedAt}) => {
  'id': id,
  'householdId': 'h1',
  'title': 'Evento $id',
  'notes': null,
  'location': null,
  'isAllDay': false,
  'startsAt': '2026-11-10T10:00:00Z',
  'endsAt': '2026-11-10T11:00:00Z',
  'startDate': null,
  'endDate': null,
  'recurrence': null,
  'participantUserIds': <String>[],
  'dropOffUserId': null,
  'pickUpUserId': null,
  'updatedAt': updatedAt,
};

Chore _chore() => Chore.fromJson({
  'id': 'c1',
  'householdId': 'h1',
  'title': 'Lixo (novo nome)',
  'notes': null,
  'points': 5,
  'dueDate': '2026-10-24',
  'recurrence': 'Daily',
  'assigneeUserId': null,
  'rotationUserIds': <String>[],
  'isDone': false,
  'updatedAt': '2026-10-24T10:00:00Z',
});

/// Conta as gravações por chave.
class _CountingStore extends MemoryKeyValueStore {
  final _writes = <String, int>{};

  int writes(String key) => _writes[key] ?? 0;

  @override
  Future<void> write(String key, String? value) {
    _writes[key] = writes(key) + 1;
    return super.write(key, value);
  }
}

/// Um PNG de 1×1 (transparente).
final _png = Uint8List.fromList(
  base64Decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=='),
);
