import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';

/// O servidor diz em que casas está o "u1" e com que papel; os testes mudam-no como faria outro membro.
class _Server {
  var role = 'Adult';
  var inHouse = true;
  var meCalls = 0;

  late final client = MockClient((request) async {
    if (request.url.path != '/api/me') return http.Response('', 404);
    meCalls++;
    return http.Response.bytes(
      utf8.encode(
        jsonEncode({
          'user': {'id': 'u1', 'email': 'pai@kubico.test', 'displayName': 'Pai', 'isChildProfile': false},
          'households': [
            if (inHouse) {'id': 'h1', 'name': 'Casa', 'role': role},
          ],
        }),
      ),
      200,
      headers: {'content-type': 'application/json; charset=utf-8'},
    );
  });
}

Member _me(HouseholdRole role) => Member(userId: 'u1', displayName: 'Pai', role: role, color: '#4F7CAC');

void main() {
  test('quando o meu papel muda noutro dispositivo, a sessão volta a ler as casas', () async {
    final server = _Server();
    final session = SessionController(
      api: ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't',
      store: MemoryTokenStore(),
    );
    await session.refresh();

    // Outro membro mudou: nada a fazer.
    await session.memberChanged(
      'h1',
      member: const Member(userId: 'u2', displayName: 'Mãe', role: HouseholdRole.adult, color: '#000000'),
    );
    // O meu papel não mudou: também não.
    await session.memberChanged('h1', member: _me(HouseholdRole.adult));
    final callsBefore = server.meCalls;

    server.role = 'Guest';
    await session.memberChanged('h1', member: _me(HouseholdRole.guest));

    expect(callsBefore, 1);
    expect(server.meCalls, 2);
    expect(session.currentHousehold!.role, HouseholdRole.guest);
  });

  test('quando me tiram da casa, a sessão fica sem ela', () async {
    final server = _Server();
    final session = SessionController(
      api: ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't',
      store: MemoryTokenStore(),
    );
    await session.refresh();
    await session.selectHousehold('h1');

    server.inHouse = false;
    await session.memberChanged('h1', removedUserId: 'u1');

    expect(session.households, isEmpty);
    expect(session.currentHousehold, isNull);
    expect(session.status, SessionStatus.signedIn);
  });
}
