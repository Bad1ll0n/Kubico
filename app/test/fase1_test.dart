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
import 'package:kubico/src/ui/activity_screen.dart';
import 'package:kubico/src/ui/calendar_event_editor.dart';
import 'package:kubico/src/ui/chore_editor.dart';
import 'package:kubico/src/ui/chores_view.dart';
import 'package:kubico/src/ui/household_screen.dart';
import 'package:kubico/src/ui/theme.dart';
import 'package:provider/provider.dart';

/// Testes das correções da fase 1 na app (tarefas, pontos, membros, convites e sessão).
http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

String _day(DateTime d) => apiDate(d);

Map<String, dynamic> _chore(
  String id,
  String title, {
  String? assignee,
  int points = 0,
  String recurrence = 'None',
  DateTime? due,
  DateTime? availableFrom,
}) => {
  'id': id,
  'householdId': 'h1',
  'title': title,
  'notes': null,
  'points': points,
  'dueDate': due == null ? null : _day(due),
  'recurrence': recurrence,
  'assigneeUserId': assignee,
  'rotationUserIds': <String>[],
  'isDone': false,
  'completedAt': null,
  'updatedAt': '2026-10-15T10:00:00Z',
  'availableFrom': availableFrom == null ? null : _day(availableFrom),
};

Map<String, dynamic> _member(String id, String name, String role, {bool child = false}) => {
  'userId': id,
  'displayName': name,
  'role': role,
  'color': '#4F7CAC',
  'isChildProfile': child,
};

/// Uma casa com a Mãe (quem usa a app, com o papel [role]) e a Rita (perfil de criança).
class _Server {
  _Server({this.role = 'Admin', this.chores = const [], this.myAccess});

  final String role;
  final List<Map<String, dynamic>> chores;
  final Map<String, String>? myAccess;
  final requests = <String>[];
  final bodies = <String, Object?>{};
  final invites = <Map<String, dynamic>>[
    {'code': 'K7M2QX9P', 'role': 'Adult', 'expiresAt': '2026-10-25T10:00:00Z', 'maxUses': 1, 'usedCount': 0},
  ];
  var pointsSpent = true;

  late final client = MockClient((request) async {
    final path = request.url.path;
    final key = '${request.method} $path${request.url.hasQuery ? '?${request.url.query}' : ''}';
    requests.add(key);
    if (request.body.isNotEmpty) bodies[key] = jsonDecode(request.body);
    switch ((request.method, path)) {
      case ('GET', '/api/me'):
        return _json({
          'user': {'id': 'mae', 'email': 'mae@kubico.test', 'displayName': 'Mãe', 'isChildProfile': false},
          'households': [
            {'id': 'h1', 'name': 'Casa', 'role': role, 'access': ?myAccess},
          ],
        });
      case ('GET', '/api/households/h1'):
        return _json({
          'id': 'h1',
          'name': 'Casa',
          'timeZoneId': 'Europe/Lisbon',
          'myRole': role,
          'myAccess': ?myAccess,
          'members': [_member('mae', 'Mãe', role), _member('rita', 'Rita', 'Child', child: true)],
        });
      case ('GET', '/api/households/h1/absences'):
        return _json(<Object>[]);
      case ('GET', '/api/households/h1/invites'):
        return _json(invites);
      case ('DELETE', '/api/households/h1/invites/K7M2QX9P'):
        invites.clear();
        return http.Response('', 204);
      case ('POST', '/api/households/h1/children/rita/sign-out'):
        return http.Response('', 204);
      case ('GET', '/api/households/h1/chores'):
        return _json(chores);
      case ('GET', '/api/households/h1/chores/stats'):
        return _json({'from': '2026-09-15', 'to': '2026-10-15', 'members': <Object>[]});
      case ('POST', '/api/households/h1/chores/c1/complete'):
        return _json({
          'chore': {...chores.first, 'isDone': true},
          'pointsEarned': 5,
        });
      case ('GET', '/api/households/h1/activity'):
        return _json([
          {
            'id': 7,
            'userId': 'rita',
            'module': 'Chores',
            'summary': 'fez «Lavar o carro»',
            'createdAt': '2026-10-19T09:00:00Z',
            'canUndo': true,
            'undoneAt': null,
            'undoneByUserId': null,
            'isUndo': false,
          },
        ]);
      case ('POST', '/api/households/h1/activity/7/undo'):
        if (request.url.queryParameters['force'] != 'true' && pointsSpent) {
          return _json({
            'title': 'Os pontos desta tarefa já foram trocados: Rita ficaria a dever 10 pontos.',
            'status': 409,
            'code': 'points-spent',
          }, 409);
        }
        return _json(<String, Object>{});
    }
    return http.Response('', 404);
  });
}

Future<SessionController> _pump(WidgetTester tester, _Server server, Widget Function(SessionController) home) async {
  tester.view.physicalSize = const Size(900, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
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
      child: MaterialApp(home: Builder(builder: (_) => home(session))),
    ),
  );
  await tester.pumpAndSettle();
  return session;
}

void main() {
  group('T9: valores que a app não conhece', () {
    test('um enum desconhecido (ou de outro tipo) fica com o valor por omissão', () {
      final chore = Chore.fromJson({..._chore('c1', 'Estranha'), 'recurrence': 9, 'timeOfDay': 'Tarde'});

      expect(chore.recurrence, ChoreRecurrence.none);
      expect(chore.timeOfDay, ChoreTimeOfDay.any);
      expect(HouseholdRole.fromApi(3), HouseholdRole.guest);
    });

    test('uma tarefa que não se consegue ler fica de fora, e as outras aparecem', () async {
      final api = ApiClient(
        baseUrl: 'http://api',
        httpClient: MockClient(
          (_) async => _json([
            _chore('c1', 'Boa'),
            {'id': 'c2', 'title': 42},
          ]),
        ),
      );

      final chores = await api.getChores('h1');

      expect(chores.map((c) => c.title), ['Boa']);
    });

    test('um erro técnico mostra-se como uma frase simples; os do servidor como vêm', () {
      expect(errorText(TypeError()), 'Ocorreu um erro inesperado. Tenta outra vez.');
      expect(errorText(const ApiException(409, 'Esta tarefa já está concluída.')), 'Esta tarefa já está concluída.');
    });

    test('o código do erro do servidor chega à app', () async {
      final api = ApiClient(
        baseUrl: 'http://api',
        httpClient: MockClient((_) async => _json({'title': 'Os pontos…', 'code': 'points-spent'}, 409)),
      );

      final error = await api.undoActivity('h1', 1).then<Object?>((_) => null, onError: (Object e) => e);

      expect(error, isA<ApiException>().having((e) => e.isPointsSpent, 'isPointsSpent', isTrue));
    });
  });

  testWidgets('T1: uma tarefa que se repete e já foi feita espera pelo seu dia', (tester) async {
    final tomorrow = DateUtils.dateOnly(DateTime.now()).add(const Duration(days: 1));
    final server = _Server(
      chores: [
        _chore('c1', 'Fazer a cama', recurrence: 'Daily', due: tomorrow, availableFrom: tomorrow, assignee: 'mae'),
      ],
    );
    await _pump(tester, server, (session) => ChoresView(household: session.currentHousehold!));

    final tooltip =
        'Já está feita. Volta a estar disponível a ${tomorrow.day.toString().padLeft(2, '0')}/'
        '${tomorrow.month.toString().padLeft(2, '0')}.';
    final button = tester.widget<IconButton>(
      find.ancestor(of: find.byTooltip(tooltip), matching: find.byType(IconButton)),
    );

    expect(button.onPressed, isNull);
    expect(find.textContaining('disponível a'), findsOneWidget);
  });

  testWidgets('A1: a mãe marca a tarefa da Rita e escolhe quem a fez', (tester) async {
    final server = _Server(chores: [_chore('c1', 'Arrumar o quarto', assignee: 'rita', points: 5)]);
    await _pump(tester, server, (session) => ChoresView(household: session.currentHousehold!));

    await tester.tap(find.byTooltip('Marcar como feita'));
    await tester.pumpAndSettle();
    expect(find.text('Quem fez «Arrumar o quarto»?'), findsOneWidget);
    await tester.tap(find.text('Rita').last);
    await tester.pumpAndSettle();

    expect(server.bodies['POST /api/households/h1/chores/c1/complete'], {'doneByUserId': 'rita'});
    expect(find.text('+5 pontos para Rita!'), findsOneWidget);
  });

  testWidgets('A11: escolher uma repetição numa tarefa sem prazo põe o prazo em hoje', (tester) async {
    ChoreEditorResult? result;
    await _pump(
      tester,
      _Server(),
      (session) => Builder(
        builder: (context) => TextButton(
          onPressed: () async => result = await showChoreEditor(context, members: const []),
          child: const Text('abrir'),
        ),
      ),
    );
    await tester.tap(find.text('abrir'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField).first, 'Pôr a mesa');
    await tester.tap(find.text('Não se repete'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Todos os dias').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    final draft = (result! as ChoreSaved).draft;
    expect(draft.recurrence, ChoreRecurrence.daily);
    expect(draft.dueDate, DateUtils.dateOnly(DateTime.now()));
  });

  testWidgets('A7 e S6: não se convida para administrador; convites ativos anulam-se; sessões da criança terminam', (
    tester,
  ) async {
    final server = _Server();
    await _pump(tester, server, (_) => const HouseholdScreen(householdId: 'h1'));

    expect(find.text('Convites ativos'), findsOneWidget);
    expect(find.textContaining('0 de 1 utilizações'), findsOneWidget);
    await tester.tap(find.text('Anular'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Anular'));
    await tester.pumpAndSettle();
    expect(server.requests, contains('DELETE /api/households/h1/invites/K7M2QX9P'));
    expect(find.text('Convites ativos'), findsNothing);

    await tester.tap(find.text('Convidar'));
    await tester.pumpAndSettle();
    expect(find.text('Convidar como…'), findsOneWidget);
    expect(find.descendant(of: find.byType(BottomSheet), matching: find.text('Administrador')), findsNothing);
    expect(find.descendant(of: find.byType(BottomSheet), matching: find.text('Adulto')), findsOneWidget);
    await tester.tapAt(const Offset(10, 10));
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Opções de Rita'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Terminar sessões'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Terminar sessões'));
    await tester.pumpAndSettle();
    expect(server.requests, contains('POST /api/households/h1/children/rita/sign-out'));
    expect(find.text('As sessões de Rita terminaram.'), findsOneWidget);
  });

  testWidgets('A4: quem só vê as tarefas marca as suas ausências, mas não as dos outros', (tester) async {
    final server = _Server(
      role: 'Adult',
      myAccess: {'Shopping': 'Manage', 'Chores': 'View', 'Calendar': 'Manage', 'Rewards': 'Manage'},
    );
    await _pump(tester, server, (_) => const HouseholdScreen(householdId: 'h1'));

    await tester.tap(find.byTooltip('Opções de Mãe'));
    await tester.pumpAndSettle();
    expect(find.text('Marcar ausência'), findsOneWidget);
    await tester.tapAt(const Offset(10, 10));
    await tester.pumpAndSettle();

    // A Rita (perfil de criança): sem gerir as tarefas, o adulto não lhe marca ausências (muda o PIN, sim).
    await tester.tap(find.byTooltip('Opções de Rita'));
    await tester.pumpAndSettle();
    expect(find.text('Mudar PIN'), findsOneWidget);
    expect(find.text('Marcar ausência'), findsNothing);
  });

  testWidgets('A6: as crianças não aparecem em quem leva e quem vai buscar', (tester) async {
    await _pump(
      tester,
      _Server(),
      (session) => Builder(
        builder: (context) => TextButton(
          onPressed: () => showCalendarEventEditor(
            context,
            members: [
              Member.fromJson(_member('mae', 'Mãe', 'Admin')),
              Member.fromJson(_member('rita', 'Rita', 'Child', child: true)),
            ],
            day: DateTime(2026, 11, 4),
          ),
          child: const Text('abrir'),
        ),
      ),
    );
    await tester.tap(find.text('abrir'));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(DropdownButtonFormField<String?>, 'Quem leva'));
    await tester.pumpAndSettle();

    expect(find.text('Mãe'), findsWidgets);
    expect(find.text('Rita'), findsNWidgets(1), reason: 'a Rita só aparece nos participantes');
  });

  testWidgets('T8: desfazer uma tarefa cujos pontos já foram trocados pede confirmação a quem gere', (tester) async {
    final server = _Server();
    await _pump(
      tester,
      server,
      (_) => const ActivityScreen(
        household: HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.admin),
      ),
    );

    await tester.tap(find.text('Desfazer'));
    await tester.pumpAndSettle();
    expect(find.text('Os pontos já foram trocados'), findsOneWidget);
    await tester.tap(find.text('Continuar mesmo assim'));
    await tester.pumpAndSettle();

    expect(server.requests, contains('POST /api/households/h1/activity/7/undo?force=true'));
    expect(find.text('Desfeito.'), findsOneWidget);
  });

  test('T8: um saldo negativo lê-se como uma dívida', () {
    expect(pointsText(12), '12 pts');
    expect(pointsText(-5), 'deve 5 pts');
  });

  test('S12 e S18: quando a sessão acaba noutro lado, a app apaga o que guardou; terminar em todo o lado renova o calendário', () async {
    var cleaned = 0;
    final requests = <String>[];
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((request) async {
        requests.add('${request.method} ${request.url.path} ${request.body}');
        return request.url.path == '/api/auth/refresh'
            ? _json({'title': 'A sessão terminou.'}, 401)
            : http.Response('', 204);
      }),
    )..accessToken = 't';
    final store = MemoryTokenStore()..values['kubico.refreshToken'] = 'r';
    final session = SessionController(api: api, store: store)..onSessionEnded = () async => cleaned++;

    final renewed = await session.renewSession();
    api.accessToken = 't';
    await session.signOutEverywhere(resetCalendarFeeds: true);

    expect(renewed, isFalse);
    expect(cleaned, 2);
    expect(requests, contains('POST /api/auth/logout-all {"resetCalendarFeeds":true}'));
  });
}
