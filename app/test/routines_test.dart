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
import 'package:kubico/src/ui/chore_editor.dart';
import 'package:kubico/src/ui/chores_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _routine(
  String id,
  String title,
  String timeOfDay,
  List<String> steps, {
  List<int> done = const [],
  String dueDate = '2026-10-19',
  String updatedAt = '2026-10-19T07:00:00Z',
}) => {
  'id': id,
  'householdId': 'h1',
  'title': title,
  'notes': null,
  'points': 3,
  'dueDate': dueDate,
  'recurrence': 'Daily',
  'assigneeUserId': 'kid',
  'rotationUserIds': <String>[],
  'isDone': false,
  'completedAt': null,
  'updatedAt': updatedAt,
  'timeOfDay': timeOfDay,
  'steps': steps,
  'doneSteps': done,
};

void main() {
  test('uma rotina guarda a altura do dia e os passos', () {
    const draft = ChoreDraft(
      title: 'Rotina da manhã',
      recurrence: ChoreRecurrence.daily,
      timeOfDay: ChoreTimeOfDay.morning,
      steps: ['Lavar os dentes', 'Vestir'],
    );
    final chore = Chore.fromJson(
      _routine('c1', 'Rotina da manhã', 'Morning', ['Lavar os dentes', 'Vestir'], done: [1]),
    );
    final older = Chore.fromJson({..._routine('c2', 'Lixo', 'Any', []), 'timeOfDay': null, 'steps': null});

    expect(draft.toJson(), containsPair('timeOfDay', 'Morning'));
    expect(draft.toJson(), containsPair('steps', ['Lavar os dentes', 'Vestir']));
    expect((chore.isRoutine, chore.timeOfDay), (true, ChoreTimeOfDay.morning));
    expect(chore.doneSteps, [1]);
    expect((older.isRoutine, older.timeOfDay), (false, ChoreTimeOfDay.any));
  });

  testWidgets('no editor, uma rotina nova passa a ser de todos os dias e leva a altura do dia', (tester) async {
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

    await tester.enterText(find.widgetWithText(TextField, 'O que há para fazer?'), 'Rotina da noite');
    await tester.tap(find.text('Noite'));
    await tester.tap(find.text('Adicionar passo'));
    await tester.pumpAndSettle();
    expect(find.text('Todos os dias'), findsOneWidget);
    await tester.enterText(find.widgetWithText(TextField, 'Passo'), 'Pijama');
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    final draft = (result! as ChoreSaved).draft;
    expect((draft.recurrence, draft.timeOfDay), (ChoreRecurrence.daily, ChoreTimeOfDay.evening));
    expect(draft.steps, ['Pijama']);
  });

  testWidgets('a criança marca os passos da rotina e o último conclui-a', (tester) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    final requests = <String>[];
    var morning = _routine('c1', 'Rotina da manhã', 'Morning', ['Lavar os dentes', 'Fazer a cama']);
    final client = MockClient((request) async {
      final path = request.url.path;
      if (path == '/api/me') {
        return _json({
          'user': {'id': 'kid', 'email': null, 'displayName': 'Rita', 'isChildProfile': true},
          'households': [
            {'id': 'h1', 'name': 'Casa', 'role': 'Child'},
          ],
        });
      }
      if (path == '/api/households/h1') {
        return _json({
          'id': 'h1',
          'name': 'Casa',
          'timeZoneId': 'Europe/Lisbon',
          'myRole': 'Child',
          'members': [
            {'userId': 'kid', 'displayName': 'Rita', 'role': 'Child', 'color': '#E07A5F'},
          ],
        });
      }
      if (path == '/api/households/h1/chores') {
        return _json([
          _routine('c2', 'Rotina da noite', 'Evening', ['Pijama']),
          morning,
        ]);
      }
      if (path == '/api/households/h1/chores/stats') {
        return _json({'from': '2026-09-19', 'to': '2026-10-19', 'members': []});
      }
      if (path.startsWith('/api/households/h1/chores/c1/steps/')) {
        final index = int.parse(path.split('/').last);
        requests.add('$index ${request.body}');
        final done = {...(morning['doneSteps'] as List).cast<int>(), index}.toList()..sort();
        if (done.length == 2) {
          morning = _routine(
            'c1',
            'Rotina da manhã',
            'Morning',
            ['Lavar os dentes', 'Fazer a cama'],
            dueDate: '2026-10-20',
            updatedAt: '2026-10-19T07:10:00Z',
          );
          return _json({'chore': morning, 'completed': true, 'pointsEarned': 3});
        }
        morning = {...morning, 'doneSteps': done, 'updatedAt': '2026-10-19T07:05:00Z'};
        return _json({'chore': morning, 'completed': false, 'pointsEarned': 0});
      }
      return http.Response('', 404);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
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
        child: MaterialApp(home: ChoresView(household: session.currentHousehold!)),
      ),
    );
    await tester.pumpAndSettle();

    // No mesmo dia, a rotina da manhã aparece antes da da noite.
    final morningTop = tester.getTopLeft(find.text('Rotina da manhã')).dy;
    final eveningTop = tester.getTopLeft(find.text('Rotina da noite')).dy;
    expect(morningTop, lessThan(eveningTop));
    expect(find.textContaining('0/2 passos'), findsOneWidget);

    await tester.tap(find.text('Lavar os dentes'));
    await tester.pumpAndSettle();
    expect(requests, ['0 {"done":true}']);
    expect(find.textContaining('1/2 passos'), findsOneWidget);

    await tester.tap(find.text('Fazer a cama'));
    await tester.pumpAndSettle();
    expect(requests.last, '1 {"done":true}');
    expect(find.text('Rotina da manhã: +3 pontos!'), findsOneWidget);
    expect(find.textContaining('0/2 passos'), findsOneWidget, reason: 'amanhã recomeça do primeiro passo');
  });
}
