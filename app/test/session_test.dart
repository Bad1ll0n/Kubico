import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

const _user = {'id': 'u1', 'email': 'filipe@kubico.test', 'displayName': 'Filipe'};

/// Servidor onde o token de acesso "velho" já expirou e o refresh token "r1" ainda serve (uma vez).
class _Server {
  var refreshes = 0;
  var refreshAccepted = true;
  final calls = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    calls.add('${request.method} $path');
    if (path == '/api/auth/refresh') {
      refreshes++;
      final body = jsonDecode(request.body) as Map<String, dynamic>;
      if (!refreshAccepted || body['refreshToken'] != 'r1') {
        return _json({'title': 'A sessão terminou. Entra novamente.'}, 401);
      }
      return _json({
        'accessToken': 'novo',
        'expiresAt': DateTime.now().toUtc().add(const Duration(minutes: 15)).toIso8601String(),
        'user': _user,
        'refreshToken': 'r2',
        'refreshTokenExpiresAt': '2030-01-01T00:00:00Z',
      });
    }
    if (path == '/api/auth/logout' || path == '/api/auth/logout-all') return http.Response('', 204);
    if (request.headers['Authorization'] != 'Bearer novo') return http.Response('', 401);
    if (path == '/api/me') return _json({'user': _user, 'households': <Object>[]});
    if (path == '/api/households/h1') return http.Response('', 404);
    return http.Response('', 404);
  });
}

Future<(SessionController, MemoryTokenStore)> _restore(_Server server) async {
  final store = MemoryTokenStore()
    ..values['kubico.accessToken'] = 'velho'
    ..values['kubico.refreshToken'] = 'r1';
  final session = SessionController(
    api: ApiClient(baseUrl: 'http://api', httpClient: server.client),
    store: store,
  );
  await session.restore();
  return (session, store);
}

void main() {
  test('com o token de acesso expirado, a sessão renova-se sozinha e o pedido repete-se', () async {
    final server = _Server();

    final (session, store) = await _restore(server);

    expect(session.status, SessionStatus.signedIn);
    expect(session.user?.displayName, 'Filipe');
    expect(store.values['kubico.accessToken'], 'novo');
    expect(store.values['kubico.refreshToken'], 'r2');
    expect(server.calls, ['GET /api/me', 'POST /api/auth/refresh', 'GET /api/me']);
  });

  test('vários pedidos com o token expirado renovam a sessão uma só vez', () async {
    final server = _Server();
    final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 'velho';
    final store = MemoryTokenStore()..values['kubico.refreshToken'] = 'r1';
    SessionController(api: api, store: store);

    await Future.wait([api.getMe(), api.getMe(), api.getMe()]);

    expect(server.refreshes, 1);
  });

  test('se o servidor recusar a renovação, a sessão termina e os tokens são apagados', () async {
    final server = _Server()..refreshAccepted = false;

    final (session, store) = await _restore(server);

    expect(session.status, SessionStatus.signedOut);
    expect(store.values, isEmpty);
  });

  test('terminar sessão avisa o servidor; "em todos os dispositivos" usa o logout-all', () async {
    final server = _Server();
    final (session, store) = await _restore(server);

    await session.signOut();
    expect(server.calls.last, 'POST /api/auth/logout');
    expect(store.values.containsKey('kubico.refreshToken'), isFalse);

    final (other, _) = await _restore(server..refreshAccepted = true);
    await other.signOutEverywhere();
    expect(server.calls.last, 'POST /api/auth/logout-all');
    expect(other.status, SessionStatus.signedOut);
  });

  test('o tempo real pede um token que ainda dure', () async {
    final server = _Server();
    final (session, _) = await _restore(server);
    final before = server.refreshes;

    expect(await session.freshAccessToken(), 'novo');
    expect(server.refreshes, before, reason: 'o token renovado ainda dura 15 minutos');
  });
}
