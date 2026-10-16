import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/calendar_sync_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _connection(String provider, {String status = 'Active', String? lastError}) => {
  'id': 'c-$provider',
  'householdId': 'h1',
  'provider': provider,
  'accountEmail': provider == 'Google' ? 'mae@gmail.com' : 'mae@outlook.com',
  'status': status,
  'lastSyncedAt': DateTime.now().toUtc().toIso8601String(),
  'lastError': lastError,
};

/// O servidor: que serviços tem, se quem pede pode ligar contas, e as ligações (que os testes mudam).
class _FakeApi {
  _FakeApi({
    this.canConnect = true,
    this.available = const ['Google', 'Microsoft'],
    List<Map<String, dynamic>>? connections,
  }) : connections = connections ?? [];

  final bool canConnect;
  final List<String> available;
  final List<Map<String, dynamic>> connections;
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    requests.add('${request.method} $path');
    if (path == '/api/households/h1/calendar-sync') {
      return _json({
        'canConnect': canConnect,
        'providers': [
          for (final p in ['Google', 'Microsoft']) {'provider': p, 'available': available.contains(p)},
        ],
        'connections': connections,
      });
    }
    if (path.endsWith('/authorize')) {
      return _json({'url': 'https://accounts.google.com/o/oauth2/v2/auth?state=abc'});
    }
    if (path.endsWith('/sync')) {
      return _json(
        _connection('Google', lastError: '«Reunião mensal» repete-se de uma forma que o Kubico ainda não suporta.'),
      );
    }
    if (request.method == 'DELETE') {
      connections.removeWhere((c) => path.endsWith(c['id'] as String));
      return http.Response('', 204);
    }
    return http.Response('', 404);
  });
}

Future<(HouseholdRealtime, List<Uri>)> _pump(WidgetTester tester, _FakeApi api) async {
  final realtime = HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't');
  final opened = <Uri>[];
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(
          value: ApiClient(baseUrl: 'http://api', httpClient: api.client)..accessToken = 't',
        ),
        Provider.value(value: realtime),
      ],
      child: MaterialApp(
        home: CalendarSyncScreen(
          householdId: 'h1',
          openUrl: (url) async {
            opened.add(url);
            return true;
          },
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (realtime, opened);
}

void main() {
  testWidgets('um adulto liga a conta Microsoft no navegador e vê a conta Google já ligada', (tester) async {
    final api = _FakeApi(connections: [_connection('Google')]);
    final (_, opened) = await _pump(tester, api);

    expect(find.text('mae@gmail.com'), findsOneWidget);
    expect(find.textContaining('Sincronizado hoje às'), findsOneWidget);
    expect(find.text('Ligar a conta Google'), findsNothing);

    await tester.tap(find.text('Ligar a conta Microsoft'));
    await tester.pumpAndSettle();

    expect(api.requests, contains('POST /api/households/h1/calendar-sync/microsoft/authorize'));
    expect(opened.single.host, 'accounts.google.com');
    expect(find.text('Autoriza o Kubico na página que abriu e depois volta aqui.'), findsOneWidget);
  });

  testWidgets('sincronizar agora mostra o que ficou de fora, e desligar pede confirmação', (tester) async {
    final api = _FakeApi(connections: [_connection('Google')]);
    await _pump(tester, api);

    await tester.tap(find.text('Sincronizar agora'));
    await tester.pumpAndSettle();
    expect(api.requests, contains('POST /api/households/h1/calendar-sync/c-Google/sync'));
    expect(find.text('«Reunião mensal» repete-se de uma forma que o Kubico ainda não suporta.'), findsOneWidget);

    await tester.tap(find.text('Desligar'));
    await tester.pumpAndSettle();
    expect(find.text('Desligar a conta Google?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Desligar'));
    await tester.pumpAndSettle();

    expect(api.requests, contains('DELETE /api/households/h1/calendar-sync/c-Google'));
    expect(find.text('mae@gmail.com'), findsNothing);
    expect(find.text('Ligar a conta Google'), findsOneWidget);
  });

  testWidgets('uma conta sem acesso pede para voltar a ligar', (tester) async {
    final api = _FakeApi(connections: [_connection('Google', status: 'NeedsReconnect')]);
    await _pump(tester, api);

    expect(find.text('O acesso à conta expirou ou foi retirado. Volta a ligar a conta.'), findsOneWidget);
    expect(find.text('Sincronizar agora'), findsNothing);

    await tester.tap(find.text('Voltar a ligar'));
    await tester.pumpAndSettle();
    expect(api.requests, contains('POST /api/households/h1/calendar-sync/google/authorize'));
  });

  testWidgets('as crianças não ligam contas e um serviço sem credenciais aparece indisponível', (tester) async {
    final api = _FakeApi(canConnect: false, available: ['Google']);
    await _pump(tester, api);

    expect(find.text('Só os adultos da casa podem ligar contas.'), findsOneWidget);
    expect(find.text('Ainda não está disponível neste servidor.'), findsOneWidget);
    expect(find.byType(FilledButton), findsNothing);
  });

  testWidgets('volta a ler as ligações quando o servidor avisa que mudaram', (tester) async {
    final api = _FakeApi();
    final (realtime, _) = await _pump(tester, api);
    expect(find.text('mae@gmail.com'), findsNothing);

    api.connections.add(_connection('Google'));
    realtime.emitForTest(const CalendarConnectionsChanged());
    await tester.pumpAndSettle();

    expect(find.text('mae@gmail.com'), findsOneWidget);
  });
}
