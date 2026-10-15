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
import 'package:kubico/src/ui/chores_view.dart';
import 'package:provider/provider.dart';

Chore _chore({String id = 'c1', DateTime? due, String? assignee, int points = 0, bool done = false}) => Chore(
  id: id,
  householdId: 'h1',
  title: 'Lixo',
  notes: null,
  points: points,
  dueDate: due,
  recurrence: ChoreRecurrence.weekly,
  assigneeUserId: assignee,
  rotationUserIds: const [],
  isDone: done,
  updatedAt: DateTime(2026),
);

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _choreJson(String id, String title, {String? assignee, int points = 0}) => {
  'id': id,
  'householdId': 'h1',
  'title': title,
  'notes': null,
  'points': points,
  'dueDate': null,
  'recurrence': 'None',
  'assigneeUserId': assignee,
  'rotationUserIds': <String>[],
  'isDone': false,
  'completedAt': null,
  'updatedAt': '2026-10-15T10:00:00Z',
};

void main() {
  group('choreGroupFor', () {
    final today = DateTime(2026, 10, 15, 18, 30);

    test('separa por prazo relativamente a hoje', () {
      expect(choreGroupFor(_chore(due: DateTime(2026, 10, 14)), today), ChoreGroup.overdue);
      expect(choreGroupFor(_chore(due: DateTime(2026, 10, 15)), today), ChoreGroup.today);
      expect(choreGroupFor(_chore(due: DateTime(2026, 10, 22)), today), ChoreGroup.thisWeek);
      expect(choreGroupFor(_chore(due: DateTime(2026, 10, 23)), today), ChoreGroup.later);
      expect(choreGroupFor(_chore(), today), ChoreGroup.noDate);
    });
  });

  test('ChoreDraft envia o prazo como data sem hora', () {
    final json = ChoreDraft(title: 'Lixo', dueDate: DateTime(2026, 3, 7), recurrence: ChoreRecurrence.weekly).toJson();

    expect(json['dueDate'], '2026-03-07');
    expect(json['recurrence'], 'Weekly');
    expect(json.containsKey('assigneeUserId'), isFalse);
  });

  testWidgets('criança só pode concluir as suas tarefas e vê os pontos ganhos', (tester) async {
    final completed = <String>[];
    final server = MockClient((request) async {
      final path = request.url.path;
      if (path == '/api/me') {
        return _json({
          'user': {'id': 'kid', 'email': 'kid@kubico.test', 'displayName': 'Rita'},
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
            {'userId': 'mom', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#4F7CAC'},
            {'userId': 'kid', 'displayName': 'Rita', 'role': 'Child', 'color': '#E07A5F'},
          ],
        });
      }
      if (path == '/api/households/h1/chores') {
        return _json([
          _choreJson('c1', 'Pagar a luz', assignee: 'mom'),
          _choreJson('c2', 'Fazer a cama', assignee: 'kid', points: 2),
        ]);
      }
      if (path == '/api/households/h1/chores/stats') {
        return _json({'from': '2026-09-15', 'to': '2026-10-15', 'members': []});
      }
      if (path == '/api/households/h1/chores/c2/complete') {
        completed.add('c2');
        return _json({
          'chore': {..._choreJson('c2', 'Fazer a cama', assignee: 'kid', points: 2), 'isDone': true},
          'pointsEarned': 2,
        });
      }
      return http.Response('', 404);
    });

    final api = ApiClient(baseUrl: 'http://api', httpClient: server)..accessToken = 't';
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

    expect(find.text('Pagar a luz'), findsOneWidget);
    expect(find.text('Fazer a cama'), findsOneWidget);
    expect(find.text('Nova tarefa'), findsNothing, reason: 'crianças não criam tarefas');

    final otherButton = tester.widget<IconButton>(
      find.ancestor(of: find.byTooltip('Atribuída a outra pessoa'), matching: find.byType(IconButton)),
    );
    expect(otherButton.onPressed, isNull);

    await tester.tap(find.byTooltip('Marcar como feita'));
    await tester.pumpAndSettle();

    expect(completed, ['c2']);
    expect(find.text('+2 pontos!'), findsOneWidget);
    expect(find.text('Fazer a cama'), findsNothing);
  });
}
