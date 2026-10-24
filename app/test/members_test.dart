import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/household_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _member(String id, String name, String role, {bool child = false}) => {
  'userId': id,
  'displayName': name,
  'role': role,
  'color': '#4F7CAC',
  'isChildProfile': child,
};

/// O servidor da casa "Casa": quem está a usar a app ([me]) e os membros, que os pedidos mudam.
class _Server {
  _Server({required this.me, required this.members});

  final String me;
  final List<Map<String, dynamic>> members;
  final requests = <String>[];
  final bodies = <Map<String, dynamic>>[];
  final absences = <Map<String, dynamic>>[];
  final invites = <Map<String, dynamic>>[];
  var households = [
    {'id': 'h1', 'name': 'Casa'},
  ];

  String _role(String userId) => members.firstWhere((m) => m['userId'] == userId)['role'] as String;

  late final client = MockClient((request) async {
    final path = request.url.path;
    requests.add('${request.method} $path');
    if (request.body.isNotEmpty) bodies.add(jsonDecode(request.body) as Map<String, dynamic>);
    if (path == '/api/me') {
      return _json({
        'user': {'id': me, 'email': '$me@kubico.test', 'displayName': me, 'isChildProfile': false},
        'households': [
          for (final h in households) {...h, 'role': _role(me)},
        ],
      });
    }
    if (path == '/api/households/h1') {
      return _json({
        'id': 'h1',
        'name': 'Casa',
        'timeZoneId': 'Europe/Lisbon',
        'myRole': _role(me),
        'members': members,
      });
    }
    if (path == '/api/households/h1/invites') {
      return _json(invites);
    }
    if (path.startsWith('/api/households/h1/invites/') && request.method == 'DELETE') {
      invites.removeWhere((i) => path.endsWith(i['code'] as String));
      return http.Response('', 204);
    }
    if (path.endsWith('/sign-out')) return http.Response('', 204);
    if (path == '/api/households/h1/absences') {
      if (request.method == 'POST') {
        final b = bodies.last;
        final absence = {
          'id': 'a${absences.length + 1}',
          'householdId': 'h1',
          'userId': b['userId'],
          'startDate': b['startDate'],
          'endDate': b['endDate'],
          'reason': b['reason'],
        };
        absences.add(absence);
        return _json(absence, 201);
      }
      return _json(absences);
    }
    if (path.startsWith('/api/households/h1/absences/') && request.method == 'DELETE') {
      absences.removeWhere((a) => path.endsWith(a['id'] as String));
      return http.Response('', 204);
    }
    if (path.endsWith('/role')) {
      final id = path.split('/')[5];
      final member = members.firstWhere((m) => m['userId'] == id)..['role'] = (bodies.last['role'] as String);
      return _json(member);
    }
    if (path == '/api/households/h1/transfer-admin') {
      members.firstWhere((m) => m['userId'] == bodies.last['userId'])['role'] = 'Admin';
      members.firstWhere((m) => m['userId'] == me)['role'] = 'Adult';
      return http.Response('', 204);
    }
    if (path == '/api/households/h1/children' && request.method == 'POST') {
      final created = _member('rita', bodies.last['displayName'] as String, 'Child', child: true);
      members.add(created);
      return _json(created, 201);
    }
    if (path == '/api/households/h1/family-code') {
      return request.method == 'DELETE' ? http.Response('', 204) : _json({'code': 'FAMILIA23'});
    }
    if (path.endsWith('/pin')) return http.Response('', 204);
    if (path == '/api/households/h1/leave') {
      households = [];
      return http.Response('', 204);
    }
    if (path.startsWith('/api/households/h1/members/') && request.method == 'DELETE') {
      members.removeWhere((m) => path.endsWith(m['userId'] as String));
      return http.Response('', 204);
    }
    return http.Response('', 404);
  });
}

Future<SessionController> _pump(WidgetTester tester, _Server server, {DateTimeRange? absenceDates}) async {
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
      child: MaterialApp(
        home: Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) => HouseholdScreen(householdId: 'h1', pickAbsenceDates: (_) async => absenceDates),
                ),
              ),
              child: const Text('Abrir membros'),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.tap(find.text('Abrir membros'));
  await tester.pumpAndSettle();
  return session;
}

Future<void> _choose(WidgetTester tester, String member, String option) async {
  await tester.tap(find.byTooltip('Opções de $member'));
  await tester.pumpAndSettle();
  await tester.tap(find.text(option).last);
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('o administrador muda papéis, passa a administração e tira alguém da casa', (tester) async {
    final server = _Server(
      me: 'mae',
      members: [_member('mae', 'Mãe', 'Admin'), _member('pai', 'Pai', 'Adult'), _member('ze', 'Zé', 'Guest')],
    );
    await _pump(tester, server);

    expect(find.text('Mãe (tu)'), findsOneWidget);

    await _choose(tester, 'Zé', 'Mudar papel');
    await tester.tap(find.text('Adulto').last);
    await tester.pumpAndSettle();
    expect(server.requests, contains('PUT /api/households/h1/members/ze/role'));
    expect(server.bodies.last, {'role': 'Adult'});
    expect(find.text('Zé passou a adulto.'), findsOneWidget);

    await _choose(tester, 'Zé', 'Tirar da casa');
    expect(find.text('Tirar Zé da casa?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Tirar da casa'));
    await tester.pumpAndSettle();
    expect(server.requests, contains('DELETE /api/households/h1/members/ze'));
    expect(find.text('Zé'), findsNothing);

    await _choose(tester, 'Pai', 'Passar a administração');
    await tester.tap(find.widgetWithText(FilledButton, 'Passar'));
    await tester.pumpAndSettle();
    expect(server.bodies.last, {'userId': 'pai'});
    expect(find.text('Convidar'), findsNothing, reason: 'a Mãe deixou de ser administradora');
    // Já não gere membros: sobre o Pai, só pode marcar ausências.
    await tester.tap(find.byTooltip('Opções de Pai'));
    await tester.pumpAndSettle();
    expect(find.text('Marcar ausência'), findsOneWidget);
    expect(find.text('Tirar da casa'), findsNothing);
  });

  testWidgets('um adulto cria um perfil de criança com PIN e vê o código da família', (tester) async {
    final server = _Server(me: 'pai', members: [_member('mae', 'Mãe', 'Admin'), _member('pai', 'Pai', 'Adult')]);
    await _pump(tester, server);

    await tester.tap(find.text('Criar perfil de criança'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextFormField, 'Nome'), 'Rita');
    await tester.enterText(find.widgetWithText(TextFormField, 'PIN (4 a 6 algarismos)'), '12');
    await tester.enterText(find.widgetWithText(TextFormField, 'Repetir o PIN'), '12');
    await tester.tap(find.text('Criar perfil'));
    await tester.pumpAndSettle();
    expect(find.text('O PIN tem de ter 4 a 6 algarismos.'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextFormField, 'PIN (4 a 6 algarismos)'), '1234');
    await tester.enterText(find.widgetWithText(TextFormField, 'Repetir o PIN'), '1234');
    await tester.tap(find.text('Criar perfil'));
    await tester.pumpAndSettle();

    expect(server.requests, contains('POST /api/households/h1/children'));
    expect(server.bodies.firstWhere((b) => b.containsKey('pin')), {'displayName': 'Rita', 'pin': '1234'});
    expect(find.text('Criança · entra com PIN'), findsOneWidget);
    expect(find.text('FAMILIA23'), findsOneWidget);

    // Um adulto muda o PIN, mas só o administrador tira perfis.
    await tester.tap(find.byTooltip('Opções de Rita'));
    await tester.pumpAndSettle();
    expect(find.text('Apagar perfil'), findsNothing);
    await tester.tap(find.text('Mudar PIN'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextFormField, 'PIN (4 a 6 algarismos)'), '9876');
    await tester.enterText(find.widgetWithText(TextFormField, 'Repetir o PIN'), '9876');
    await tester.tap(find.widgetWithText(FilledButton, 'Mudar PIN'));
    await tester.pumpAndSettle();
    expect(server.requests, contains('PUT /api/households/h1/children/rita/pin'));
    expect(find.text('O PIN de Rita foi mudado e as sessões abertas terminaram.'), findsOneWidget);
  });

  testWidgets('o único administrador tem de passar a administração antes de sair', (tester) async {
    final server = _Server(me: 'mae', members: [_member('mae', 'Mãe', 'Admin'), _member('pai', 'Pai', 'Adult')]);
    await _pump(tester, server);

    await tester.tap(find.text('Sair desta casa'));
    await tester.pumpAndSettle();

    expect(find.text('Antes de saíres'), findsOneWidget);
    expect(server.requests, isNot(contains('POST /api/households/h1/leave')));
  });

  testWidgets('um adulto sai da casa e volta ao ecrã anterior sem casas', (tester) async {
    final server = _Server(me: 'pai', members: [_member('mae', 'Mãe', 'Admin'), _member('pai', 'Pai', 'Adult')]);
    final session = await _pump(tester, server);

    await tester.tap(find.text('Sair desta casa'));
    await tester.pumpAndSettle();
    expect(find.text('Sair de Casa?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Sair'));
    await tester.pumpAndSettle();

    expect(server.requests, contains('POST /api/households/h1/leave'));
    expect(session.households, isEmpty);
    expect(find.text('Abrir membros'), findsOneWidget);
  });

  testWidgets('cada um marca a sua ausência e pode cancelá-la', (tester) async {
    final server = _Server(me: 'pai', members: [_member('mae', 'Mãe', 'Admin'), _member('pai', 'Pai', 'Adult')]);
    final today = DateUtils.dateOnly(DateTime.now());
    final end = today.add(const Duration(days: 3));
    await _pump(
      tester,
      server,
      absenceDates: DateTimeRange(start: today, end: end),
    );

    await _choose(tester, 'Pai', 'Marcar ausência');
    expect(find.text('Pai vai estar fora'), findsOneWidget);
    await tester.enterText(find.widgetWithText(TextField, 'Motivo (opcional)'), 'Férias');
    await tester.tap(find.widgetWithText(FilledButton, 'Marcar ausência'));
    await tester.pumpAndSettle();

    String day(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
    expect(server.bodies.last, {'userId': 'pai', 'startDate': day(today), 'endDate': day(end), 'reason': 'Férias'});
    final until = '${end.day.toString().padLeft(2, '0')}/${end.month.toString().padLeft(2, '0')}';
    expect(find.text('Adulto · fora até $until'), findsOneWidget);

    await _choose(tester, 'Pai', 'Cancelar ausência');
    expect(server.requests, contains('DELETE /api/households/h1/absences/a1'));
    expect(find.text('Adulto'), findsOneWidget);
  });

  testWidgets('um convidado só vê a lista, sem opções nem perfis de criança', (tester) async {
    final server = _Server(
      me: 'ze',
      members: [
        _member('mae', 'Mãe', 'Admin'),
        _member('ze', 'Zé', 'Guest'),
        _member('rita', 'Rita', 'Child', child: true),
      ],
    );
    await _pump(tester, server);

    // Só as suas próprias opções (marcar ausência); nada sobre os outros.
    expect(find.byType(PopupMenuButton<String>), findsOneWidget);
    expect(find.byTooltip('Opções de Zé'), findsOneWidget);
    expect(find.text('Criar perfil de criança'), findsNothing);
    expect(find.text('Sair desta casa'), findsOneWidget);
  });
}
