import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/activity_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _entry(
  int id,
  String user,
  String? module,
  String summary, {
  bool canUndo = false,
  String? undoneBy,
}) => {
  'id': id,
  'userId': user,
  'module': module,
  'summary': summary,
  'createdAt': '2026-10-19T09:${id.toString().padLeft(2, '0')}:00Z',
  'canUndo': canUndo,
  'undoneAt': undoneBy == null ? null : '2026-10-19T10:00:00Z',
  'undoneByUserId': undoneBy,
  'isUndo': false,
};

void main() {
  testWidgets('o histórico mostra quem fez o quê e desfaz o que se pode desfazer', (tester) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    final requests = <String>[];
    var undone = false;
    final client = MockClient((request) async {
      final path = request.url.path;
      if (request.method == 'POST') requests.add(path);
      if (path == '/api/households/h1') {
        return _json({
          'id': 'h1',
          'name': 'Casa',
          'timeZoneId': 'Europe/Lisbon',
          'myRole': 'Admin',
          'members': [
            {'userId': 'mum', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#4F7CAC'},
            {'userId': 'kid', 'displayName': 'Rita', 'role': 'Child', 'color': '#E07A5F'},
          ],
        });
      }
      if (path == '/api/households/h1/activity') {
        return _json([
          _entry(
            3,
            'kid',
            'Shopping',
            'pôs «Gomas» na lista «Supermercado»',
            canUndo: !undone,
            undoneBy: undone ? 'mum' : null,
          ),
          _entry(2, 'mum', 'Chores', 'fez «Regar»', canUndo: true),
          _entry(1, 'gone', null, 'entrou na casa'),
        ]);
      }
      if (path == '/api/households/h1/activity/3/undo') {
        undone = true;
        return _json(_entry(3, 'kid', 'Shopping', 'pôs «Gomas» na lista «Supermercado»'));
      }
      if (path == '/api/households/h1/activity/2/undo') {
        return _json({'title': 'Entretanto houve outras alterações a isto. Desfaz primeiro as mais recentes.'}, 409);
      }
      return http.Response('', 404);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
          ),
        ],
        child: const MaterialApp(
          home: ActivityScreen(
            household: HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.admin),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Rita pôs «Gomas» na lista «Supermercado»'), findsOneWidget);
    expect(find.text('Antigo membro entrou na casa'), findsOneWidget);
    expect(find.text('Desfazer'), findsNWidgets(2));

    await tester.tap(find.text('Desfazer').first);
    await tester.pumpAndSettle();
    expect(requests, ['/api/households/h1/activity/3/undo']);
    expect(find.text('Desfeito.'), findsOneWidget);
    expect(find.textContaining('desfeito por Mãe'), findsOneWidget);

    await tester.tap(find.text('Desfazer'));
    await tester.pumpAndSettle();
    expect(find.text('Entretanto houve outras alterações a isto. Desfaz primeiro as mais recentes.'), findsOneWidget);
  });
}
