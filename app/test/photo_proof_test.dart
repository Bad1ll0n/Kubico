import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/chore_editor.dart';
import 'package:kubico/src/ui/chore_history_screen.dart';
import 'package:kubico/src/ui/chores_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Uma imagem PNG de 1×1.
final _png = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
);

Map<String, dynamic> _chore({bool done = false}) => {
  'id': 'c1',
  'householdId': 'h1',
  'title': 'Arrumar o quarto',
  'notes': null,
  'points': 5,
  'dueDate': null,
  'recurrence': 'None',
  'assigneeUserId': 'kid',
  'rotationUserIds': <String>[],
  'isDone': done,
  'completedAt': null,
  'updatedAt': done ? '2026-10-19T10:05:00Z' : '2026-10-19T10:00:00Z',
  'requiresPhoto': true,
};

/// O servidor da casa: [me] com o papel [role]; guarda os pedidos feitos.
class _Server {
  _Server({required this.me, required this.role});

  final String me;
  final String role;
  final requests = <String>[];
  final bodies = <String>[];
  var history = [
    {
      'id': 'k1',
      'choreId': 'c1',
      'choreTitle': 'Arrumar o quarto',
      'userId': 'kid',
      'points': 5,
      'completedAt': '2026-10-19T10:05:00Z',
      'photoId': 'p1',
      'rejected': false,
    },
  ];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (path != '/api/me' && path != '/api/households/h1') requests.add('${request.method} $path');
    if (path == '/api/me') {
      return _json({
        'user': {'id': me, 'email': '$me@kubico.test', 'displayName': me},
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': role},
        ],
      });
    }
    if (path == '/api/households/h1') {
      return _json({
        'id': 'h1',
        'name': 'Casa',
        'timeZoneId': 'Europe/Lisbon',
        'myRole': role,
        'members': [
          {'userId': 'mum', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#4F7CAC'},
          {'userId': 'kid', 'displayName': 'Rita', 'role': 'Child', 'color': '#E07A5F'},
        ],
      });
    }
    if (path == '/api/households/h1/chores') return _json([_chore()]);
    if (path == '/api/households/h1/chores/stats') {
      return _json({'from': '2026-09-19', 'to': '2026-10-19', 'members': []});
    }
    if (path == '/api/households/h1/chores/photos' && request.method == 'POST') {
      bodies.add(request.headers['content-type'] ?? '');
      return _json({'id': 'p1'}, 201);
    }
    if (path == '/api/households/h1/chores/photos/p1') return http.Response.bytes(_png, 200);
    if (path == '/api/households/h1/chores/c1/complete') {
      bodies.add(request.body);
      return _json({'chore': _chore(done: true), 'pointsEarned': 5});
    }
    if (path == '/api/households/h1/chores/history') return _json(history);
    if (path == '/api/households/h1/chores/completions/k1/reject') {
      history = [
        {...history.single, 'rejected': true},
      ];
      return _json(history.single);
    }
    return http.Response('', 404);
  });
}

Future<void> _pump(WidgetTester tester, _Server server, Widget Function(SessionController) screen) async {
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
      child: MaterialApp(home: screen(session)),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('uma tarefa com foto de prova pede a foto, envia-a e só depois fica feita', (tester) async {
    final server = _Server(me: 'kid', role: 'Child');
    PickedPhoto? photo;
    await _pump(
      tester,
      server,
      (session) => ChoresView(household: session.currentHousehold!, pickPhoto: (_) async => photo),
    );
    expect(find.textContaining('com foto'), findsOneWidget);

    // Desistiu de tirar a foto: nada muda.
    await tester.tap(find.byTooltip('Marcar como feita'));
    await tester.pumpAndSettle();
    expect(server.requests.where((r) => r.startsWith('POST')), isEmpty);

    photo = (bytes: Uint8List.fromList(_png), name: 'quarto.jpg');
    await tester.tap(find.byTooltip('Marcar como feita'));
    await tester.pumpAndSettle();

    expect(server.requests.where((r) => r.startsWith('POST')), [
      'POST /api/households/h1/chores/photos',
      'POST /api/households/h1/chores/c1/complete',
    ]);
    expect(server.bodies.first, startsWith('multipart/form-data'));
    expect(server.bodies.last, '{"photoId":"p1"}');
    expect(find.text('+5 pontos!'), findsOneWidget);
    expect(find.text('Arrumar o quarto'), findsNothing);
  });

  testWidgets('quem gere as tarefas vê a foto no histórico e pode não aceitar', (tester) async {
    final server = _Server(me: 'mum', role: 'Admin');
    await _pump(tester, server, (session) => ChoreHistoryScreen(household: session.currentHousehold!));

    expect(find.text('Arrumar o quarto'), findsOneWidget);
    expect(find.textContaining('Rita'), findsOneWidget);
    expect(server.requests, contains('GET /api/households/h1/chores/photos/p1'));

    await tester.tap(find.byTooltip('Opções de Arrumar o quarto'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Não aceitar'));
    await tester.pumpAndSettle();
    expect(find.text('Não aceitar «Arrumar o quarto»?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Não aceitar'));
    await tester.pumpAndSettle();

    expect(server.requests, contains('POST /api/households/h1/chores/completions/k1/reject'));
    expect(find.textContaining('não aceite'), findsOneWidget);
    expect(find.byTooltip('Opções de Arrumar o quarto'), findsNothing);
  });

  testWidgets('uma criança vê o histórico mas não pode recusar', (tester) async {
    final server = _Server(me: 'kid', role: 'Child');
    await _pump(tester, server, (session) => ChoreHistoryScreen(household: session.currentHousehold!));

    expect(find.text('Arrumar o quarto'), findsOneWidget);
    expect(find.byTooltip('Opções de Arrumar o quarto'), findsNothing);
  });

  testWidgets('no editor liga-se a foto de prova', (tester) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    ChoreEditorResult? result;
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () async => result = await showChoreEditor(context, members: const []),
            child: const Text('Abrir'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Abrir'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'O que há para fazer?'), 'Arrumar o quarto');
    await tester.ensureVisible(find.text('Pedir foto de prova'));
    await tester.tap(find.text('Pedir foto de prova'));
    await tester.pump();
    await tester.ensureVisible(find.text('Guardar'));
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    expect((result! as ChoreSaved).draft.toJson(), containsPair('requiresPhoto', true));
  });
}
