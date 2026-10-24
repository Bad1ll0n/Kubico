import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/notifications/notification_center.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/notifications_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _notification(String id, String title, {bool read = false}) => {
  'id': id,
  'householdId': 'h1',
  'kind': 'ChoreAssigned',
  'title': title,
  'body': 'Prazo: 15/10',
  'entityId': null,
  'createdAt': '2026-10-15T09:00:00Z',
  'isRead': read,
};

/// Servidor com dois avisos por ler e as preferências por omissão.
class _Server {
  final requests = <String>[];
  Map<String, dynamic>? savedPreferences;

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (request.method != 'GET') requests.add('${request.method} $path ${request.body}');
    if (path == '/api/notifications') {
      return _json({
        'items': [_notification('n1', 'Tarefa para ti: Lixo'), _notification('n2', 'Novo evento: Natação')],
        'unreadCount': 2,
      });
    }
    if (path == '/api/notifications/read') return http.Response('', 204);
    if (path == '/api/notifications/preferences' && request.method == 'GET') {
      return _json({
        'delivery': 'Immediate',
        'digestAt': '19:00:00',
        'quietFrom': null,
        'quietUntil': null,
        'timeZoneId': 'Europe/Lisbon',
      });
    }
    if (path == '/api/notifications/preferences') {
      savedPreferences = jsonDecode(request.body) as Map<String, dynamic>;
      return _json(savedPreferences!);
    }
    return http.Response('', 404);
  });
}

Future<(NotificationCenter, HouseholdRealtime)> _pump(WidgetTester tester, _Server server) async {
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
  final realtime = HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't');
  final center = NotificationCenter(api: api, realtime: realtime);
  await center.load();

  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: center),
      ],
      child: MaterialApp(
        home: Scaffold(appBar: AppBar(actions: const [NotificationsBell()])),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (center, realtime);
}

void main() {
  testWidgets('o sino conta os avisos por ler, incluindo os que chegam em tempo real', (tester) async {
    final (center, realtime) = await _pump(tester, _Server());
    expect(find.byTooltip('Avisos (2 por ler)'), findsOneWidget);

    realtime.emitForTest(NotificationReceived(AppNotification.fromJson(_notification('n3', 'Tens de levar: Futebol'))));
    await tester.pumpAndSettle();

    expect(center.unreadCount, 3);
    expect(center.items.first.title, 'Tens de levar: Futebol');
    expect(find.text('3'), findsOneWidget);
  });

  testWidgets('abrir um aviso marca-o como lido; "Marcar como lidos" marca todos', (tester) async {
    final server = _Server();
    final (center, _) = await _pump(tester, server);

    await tester.tap(find.byType(NotificationsBell));
    await tester.pumpAndSettle();
    expect(find.text('Tarefa para ti: Lixo'), findsOneWidget);

    await tester.tap(find.text('Tarefa para ti: Lixo'));
    await tester.pumpAndSettle();
    expect(center.unreadCount, 1);
    expect(server.requests.last, 'POST /api/notifications/read {"ids":["n1"]}');

    await tester.tap(find.text('Marcar como lidos'));
    await tester.pumpAndSettle();
    expect(center.unreadCount, 0);
    expect(server.requests.last, 'POST /api/notifications/read {}');
    expect(find.text('Marcar como lidos'), findsNothing);
  });

  testWidgets('as preferências guardam o resumo diário e as horas de silêncio', (tester) async {
    final server = _Server();
    await _pump(tester, server);

    await tester.tap(find.byType(NotificationsBell));
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Como receber os avisos'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Um resumo por dia'));
    await tester.pumpAndSettle();
    expect(find.text('Hora do resumo'), findsOneWidget);
    await tester.tap(find.text('Horas de silêncio'));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Guardar'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    expect(server.savedPreferences, {
      'delivery': 'DailyDigest',
      'digestAt': '19:00:00',
      'quietFrom': '22:00:00',
      'quietUntil': '08:00:00',
      'timeZoneId': 'Europe/Lisbon',
    });
    expect(find.text('Preferências guardadas.'), findsOneWidget);
  });
}
