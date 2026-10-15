import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/ui/devices_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _session(String id, String device, {bool current = false}) => {
  'id': id,
  'device': device,
  'startedAt': '2026-09-01T10:00:00Z',
  'lastActiveAt': '2026-10-15T09:30:00Z',
  'isCurrent': current,
};

void main() {
  testWidgets('mostra onde há sessão iniciada e termina a de outro dispositivo', (tester) async {
    final sessions = [_session('s1', 'android', current: true), _session('s2', 'windows')];
    final requests = <String>[];
    final server = MockClient((request) async {
      requests.add('${request.method} ${request.url.path} ${request.headers['X-Kubico-Device']}');
      if (request.method == 'DELETE') {
        sessions.removeWhere((s) => request.url.path.endsWith('/${s['id']}'));
        return http.Response('', 204);
      }
      return _json(sessions);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: server, deviceKind: 'android')..accessToken = 't';

    await tester.pumpWidget(
      Provider.value(
        value: api,
        child: const MaterialApp(home: DevicesScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Telemóvel ou tablet Android'), findsOneWidget);
    expect(find.text('Este dispositivo'), findsOneWidget);
    expect(find.text('Computador Windows'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Terminar'), findsOneWidget, reason: 'o próprio não se termina daqui');

    await tester.tap(find.widgetWithText(TextButton, 'Terminar'));
    await tester.pumpAndSettle();

    expect(requests, contains('DELETE /api/auth/sessions/s2 android'));
    expect(find.text('Computador Windows'), findsNothing);
    expect(find.text('Sessão terminada: computador windows.'), findsOneWidget);
  });
}
