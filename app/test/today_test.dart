import 'dart:convert';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/design/screen_awake.dart';
import 'package:kubico/src/ui/theme.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/kitchen_panel.dart';
import 'package:kubico/src/ui/today_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  200,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Terça, 10/11/2026: a Mãe, a Rita (criança) e o Rui (fora), um evento com conflito, tarefas de hoje e em atraso,
/// uma lista com compras e um pedido de recompensa à espera.
class _Server {
  _Server({required this.me, required this.role});

  final String me;
  final String role;
  final requests = <String>[];

  Map<String, dynamic> _member(String id, String name, String role, String color) => {
    'userId': id,
    'displayName': name,
    'role': role,
    'color': color,
    'isChildProfile': role == 'Child',
  };

  Map<String, dynamic> _chore(String id, String title, String? assignee, String due, {int points = 0}) => {
    'id': id,
    'householdId': 'h1',
    'title': title,
    'notes': null,
    'points': points,
    'dueDate': due,
    'recurrence': 'None',
    'assigneeUserId': assignee,
    'rotationUserIds': <String>[],
    'isDone': false,
    'updatedAt': '2026-11-01T10:00:00Z',
  };

  late final client = MockClient((request) async {
    final path = request.url.path;
    requests.add('${request.method} $path${request.body.isEmpty ? '' : ' ${request.body}'}');
    return _respond(path);
  });

  /// A resposta de uma área, já descodificada (para compor o /today).
  Object? _read(String path) => jsonDecode(utf8.decode(_respond(path).bodyBytes));

  http.Response _respond(String path) {
    return switch (path) {
      '/api/households/h1/today' => _json({
        'household': _read('/api/households/h1'),
        'absences': _read('/api/households/h1/absences'),
        'chores': _read('/api/households/h1/chores'),
        'calendar': _read('/api/households/h1/events'),
        'lists': _read('/api/households/h1/lists'),
        'rewards': _read('/api/households/h1/rewards'),
      }),
      '/api/me' => _json({
        'user': {
          'id': me,
          'email': null,
          'displayName': me == 'mae' ? 'Mãe Silva' : 'Rita',
          'isChildProfile': me != 'mae',
        },
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': role},
        ],
      }),
      '/api/households/h1' => _json({
        'id': 'h1',
        'name': 'Casa',
        'myRole': role,
        'members': [
          _member('mae', 'Mãe Silva', 'Admin', '#2F64A8'),
          _member('rita', 'Rita', 'Child', '#A9500B'),
          _member('rui', 'Rui', 'Adult', '#2E7550'),
        ],
      }),
      '/api/households/h1/absences' => _json([
        {'id': 'a1', 'userId': 'rui', 'startDate': '2026-11-09', 'endDate': '2026-11-12', 'reason': null},
      ]),
      '/api/households/h1/chores' => _json([
        _chore('c1', 'Pôr a mesa', 'rita', '2026-11-10', points: 5),
        _chore('c2', 'Regar as plantas', 'mae', '2026-11-08'),
        _chore('c3', 'Tirar o lixo', null, '2026-11-10'),
        _chore('c4', 'Aspirar', 'mae', '2026-11-12'),
      ]),
      '/api/households/h1/events' => _json({
        'events': [
          {
            'id': 'e1',
            'householdId': 'h1',
            'title': 'Natação',
            'isAllDay': false,
            'startsAt': '2026-11-10T17:30:00Z',
            'endsAt': '2026-11-10T18:30:00Z',
            'participantUserIds': ['rita'],
            'updatedAt': '2026-11-01T10:00:00Z',
          },
        ],
        'occurrences': [
          {
            'eventId': 'e1',
            'occurrenceDate': '2026-11-10',
            'startsAt': '2026-11-10T17:30:00Z',
            'endsAt': '2026-11-10T18:30:00Z',
            'startDate': null,
            'endDate': null,
            'conflictUserIds': ['mae'],
          },
        ],
        'holidays': <Object>[],
      }),
      '/api/households/h1/lists' => _json([
        {'id': 'l1', 'householdId': 'h1', 'name': 'Supermercado', 'itemCount': 14, 'checkedCount': 6},
        {'id': 'l2', 'householdId': 'h1', 'name': 'Farmácia', 'itemCount': 2, 'checkedCount': 2},
      ]),
      '/api/households/h1/rewards' => _json({
        'rewards': [
          {'id': 'r1', 'householdId': 'h1', 'title': 'Gelado', 'cost': 50, 'updatedAt': '2026-11-01T10:00:00Z'},
        ],
        'redemptions': [
          {
            'id': 'x1',
            'householdId': 'h1',
            'rewardId': 'r1',
            'userId': 'rita',
            'rewardTitle': 'Gelado',
            'points': 50,
            'status': 'Pending',
            'requestedAt': '2026-11-09T18:00:00Z',
            'updatedAt': '2026-11-09T18:00:00Z',
          },
        ],
        'points': [
          {
            'userId': 'rita',
            'displayName': 'Rita',
            'color': '#A9500B',
            'earned': 30,
            'spent': 0,
            'reserved': 0,
            'available': 30,
          },
          {
            'userId': 'mae',
            'displayName': 'Mãe',
            'color': '#2F64A8',
            'earned': 0,
            'spent': 0,
            'reserved': 0,
            'available': 0,
          },
        ],
      }),
      '/api/households/h1/lists/l1/items' => _json([
        for (final (id, name) in [('i1', 'Bananas'), ('i2', 'Leite')])
          {
            'id': id,
            'listId': 'l1',
            'name': name,
            'quantity': null,
            'category': null,
            'isChecked': false,
            'addedByUserId': 'mae',
            'createdAt': '2026-11-01T10:00:00Z',
            'updatedAt': '2026-11-01T10:00:00Z',
          },
      ]),
      '/api/households/h1/chores/c1/complete' => _json({
        'chore': {..._chore('c1', 'Pôr a mesa', 'rita', '2026-11-10', points: 5), 'isDone': true},
        'pointsEarned': 5,
      }),
      _ => http.Response('', 404),
    };
  }
}

Future<List<HouseholdModule>> _pump(WidgetTester tester, _Server server) async {
  tester.view.physicalSize = const Size(500, 2400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
  final session = SessionController(api: api, store: MemoryTokenStore());
  await session.refresh();
  final opened = <HouseholdModule>[];
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
        Provider.value(
          value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
        ),
      ],
      child: MaterialApp(
        theme: buildTheme(Brightness.light),
        home: Scaffold(
          body: TodayView(household: session.currentHousehold!, onOpen: opened.add),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return opened;
}

void main() {
  final morning = DateTime(2026, 11, 10, 9);

  testWidgets('o Hoje de um adulto: quem está fora, a agenda com o conflito, as tarefas e o que espera aprovação', (
    tester,
  ) async {
    await withClock(Clock.fixed(morning), () async {
      final semantics = tester.ensureSemantics();
      final server = _Server(me: 'mae', role: 'Admin');
      final opened = await _pump(tester, server);

      expect(find.text('Bom dia, Mãe'), findsOneWidget);
      expect(server.requests.where((r) => r.startsWith('GET /api/households/h1')), [
        'GET /api/households/h1/today',
      ], reason: 'um só pedido para o ecrã todo (F4-19)');
      // O Rui está fora: o avatar leva o selo e o leitor de ecrã di-lo.
      expect(find.byIcon(Icons.flight_takeoff_rounded), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Rui \(fora\)')), findsOneWidget);
      semantics.dispose();
      expect(find.text('Natação'), findsOneWidget);
      expect(find.text('Há um conflito neste evento.'), findsOneWidget);
      expect(find.text('Tarefas de hoje'), findsOneWidget);
      expect(find.text('1 tarefa em atraso'), findsOneWidget);
      expect(find.text('Regar as plantas'), findsOneWidget);
      expect(find.text('Pôr a mesa'), findsOneWidget);
      expect(find.text('Aspirar'), findsNothing, reason: 'é para daqui a dois dias');
      expect(find.text('Supermercado'), findsOneWidget);
      expect(find.text('8 por comprar'), findsOneWidget);
      expect(find.text('Farmácia'), findsNothing, reason: 'já está tudo comprado');
      expect(find.text('À espera de ti'), findsOneWidget);

      await tester.tap(find.text('Supermercado'));
      expect(opened, [HouseholdModule.shopping]);
    });
  });

  testWidgets('o Hoje de uma criança: as suas tarefas e os pontos até à recompensa, sem aprovações', (tester) async {
    await withClock(Clock.fixed(morning), () async {
      await _pump(tester, _Server(me: 'rita', role: 'Child'));

      expect(find.text('Bom dia, Rita'), findsOneWidget);
      expect(find.text('As minhas tarefas'), findsOneWidget);
      expect(find.text('Pôr a mesa'), findsOneWidget);
      expect(find.text('Tirar o lixo'), findsOneWidget, reason: 'é de qualquer pessoa');
      expect(find.text('Regar as plantas'), findsNothing, reason: 'é da Mãe');
      expect(find.text('Faltam 20 para «Gelado»'), findsOneWidget);
      expect(find.text('À espera de ti'), findsNothing);
    });
  });

  Future<void> pumpPanel(WidgetTester tester, _Server server) async {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
    final session = SessionController(api: api, store: MemoryTokenStore());
    await session.refresh();
    final shopping = ShoppingRepository(api: api, store: MemoryKeyValueStore(), saveDelay: Duration.zero);
    addTearDown(shopping.dispose);
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
          ChangeNotifierProvider.value(value: shopping),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
          ),
        ],
        child: MaterialApp(home: KitchenPanelScreen(household: session.currentHousehold!)),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('o painel de cozinha: agenda, tarefas, compras e pontos; conclui-se dizendo quem fez', (tester) async {
    final awake = <bool>[];
    final original = ScreenAwake.toggle;
    ScreenAwake.toggle = (on) async => awake.add(on);
    addTearDown(() => ScreenAwake.toggle = original);

    await withClock(Clock.fixed(DateTime(2026, 11, 10, 10, 5)), () async {
      final server = _Server(me: 'mae', role: 'Admin');
      await pumpPanel(tester, server);
      expect(awake, [true], reason: 'o ecrã fica ligado enquanto o painel está aberto');

      expect(find.text('10:05'), findsOneWidget);
      expect(find.text('Natação'), findsOneWidget);
      expect(find.text('Supermercado'), findsOneWidget, reason: 'a lista com mais por comprar');
      expect(find.text('Bananas'), findsOneWidget);
      expect(find.text('Pôr a mesa'), findsOneWidget);

      await tester.tap(find.byTooltip('Marcar «Pôr a mesa» como feita'));
      await tester.pumpAndSettle();
      expect(find.text('Quem fez «Pôr a mesa»?'), findsOneWidget);
      await tester.tap(find.text('Rita').last);
      await tester.pumpAndSettle();

      expect(server.requests, contains('POST /api/households/h1/chores/c1/complete {"doneByUserId":"rita"}'));
      await tester.pumpWidget(const SizedBox());
      expect(awake, [true, false], reason: 'ao sair, o ecrã volta a poder apagar-se');
    });
  });

  testWidgets('"Quem és?" no painel: as tarefas ficam em nome de quem escolheu, durante 2 minutos', (tester) async {
    var now = DateTime(2026, 11, 10, 10, 5);
    await withClock(Clock(() => now), () async {
      final server = _Server(me: 'mae', role: 'Admin');
      await pumpPanel(tester, server);

      await tester.tap(find.byKey(const ValueKey('who-rita')));
      await tester.pumpAndSettle();
      expect(find.bySemanticsLabel('Rita está a usar o painel'), findsOneWidget);

      await tester.tap(find.byTooltip('Marcar «Pôr a mesa» como feita'));
      await tester.pumpAndSettle();
      expect(find.text('Quem fez «Pôr a mesa»?'), findsNothing, reason: 'já se sabe quem está a usar o painel');
      expect(server.requests, contains('POST /api/households/h1/chores/c1/complete {"doneByUserId":"rita"}'));

      // Passados 2 minutos sem mexer, o painel volta a ser de todos.
      now = now.add(const Duration(minutes: 3));
      await tester.pump(const Duration(seconds: 21));
      expect(find.bySemanticsLabel('Rita está a usar o painel'), findsNothing);
      expect(find.bySemanticsLabel('Sou Rita'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });
  });

  testWidgets('à noite o painel mostra só o relógio e o primeiro evento de amanhã', (tester) async {
    await withClock(Clock.fixed(DateTime(2026, 11, 9, 23, 30)), () async {
      await pumpPanel(tester, _Server(me: 'mae', role: 'Admin'));

      expect(find.text('23:30'), findsOneWidget);
      expect(find.textContaining('Natação'), findsOneWidget);
      expect(find.text('Agenda'), findsNothing);
      await tester.pumpWidget(const SizedBox());
    });
  });
}
