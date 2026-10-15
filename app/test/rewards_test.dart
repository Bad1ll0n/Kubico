import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/rewards_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, dynamic> _redemption(String id, String userId, String title, int points, String status) => {
  'id': id,
  'householdId': 'h1',
  'rewardId': 'r1',
  'userId': userId,
  'rewardTitle': title,
  'points': points,
  'status': status,
  'requestedAt': '2026-10-14T18:00:00Z',
  'decidedByUserId': null,
  'decidedAt': null,
  'updatedAt': '2026-10-14T18:00:00Z',
};

Map<String, dynamic> _reward(String id, String title, int cost) => {
  'id': id,
  'householdId': 'h1',
  'title': title,
  'description': null,
  'cost': cost,
  'updatedAt': '2026-10-01T10:00:00Z',
};

Map<String, dynamic> _points(String userId, String name, int available, {int reserved = 0}) => {
  'userId': userId,
  'displayName': name,
  'color': '#4F7CAC',
  'earned': available + reserved,
  'spent': 0,
  'reserved': reserved,
  'available': available,
};

/// API falsa: a Rita tem 30 pontos e o Rui pediu um gelado, que está à espera de aprovação.
class _FakeApi {
  _FakeApi(this.me, this.role);

  final String me;
  final String role;
  final actions = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (path == '/api/me') {
      return _json({
        'user': {'id': me, 'email': '$me@kubico.test', 'displayName': me},
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': role},
        ],
      });
    }
    if (path == '/api/households/h1/rewards') {
      return _json({
        'rewards': [_reward('r1', 'Escolher o filme de sexta', 25), _reward('r2', 'Parque aquático', 100)],
        'redemptions': [_redemption('x1', 'rui', 'Um gelado', 5, 'Pending')],
        'points': [_points('rita', 'Rita', 30), _points('rui', 'Rui', 0, reserved: 5), _points('mom', 'Mãe', 12)],
      });
    }
    actions.add('${request.method} $path');
    if (path == '/api/households/h1/rewards/r1/redeem') {
      return _json(_redemption('x2', me, 'Escolher o filme de sexta', 25, 'Pending'), 201);
    }
    if (path == '/api/households/h1/redemptions/x1/approve') {
      return _json(_redemption('x1', 'rui', 'Um gelado', 5, 'Approved'));
    }
    return http.Response('', 404);
  });
}

Future<void> _pump(WidgetTester tester, _FakeApi fake) async {
  final api = ApiClient(baseUrl: 'http://api', httpClient: fake.client)..accessToken = 't';
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
      child: MaterialApp(home: RewardsView(household: session.currentHousehold!)),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('uma criança vê os seus pontos e pede uma recompensa que um adulto tem de aprovar', (tester) async {
    final fake = _FakeApi('rita', 'Child');
    await _pump(tester, fake);

    expect(find.text('30'), findsOneWidget);
    expect(find.text('Rui · 0 pts'), findsOneWidget);
    expect(find.textContaining('Faltam 70 pontos'), findsOneWidget);
    expect(find.text('Nova recompensa'), findsNothing, reason: 'só os adultos criam recompensas');
    expect(find.byTooltip('Aprovar'), findsNothing, reason: 'só os adultos aprovam pedidos');
    expect(find.widgetWithText(TextButton, 'Cancelar'), findsNothing, reason: 'o pedido do Rui não é dela');

    final expensive = tester.widget<FilledButton>(find.widgetWithText(FilledButton, '100 pts'));
    expect(expensive.onPressed, isNull);

    await tester.tap(find.widgetWithText(FilledButton, '25 pts'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Trocar'));
    await tester.pumpAndSettle();

    expect(fake.actions, ['POST /api/households/h1/rewards/r1/redeem']);
    expect(find.text('Pedido enviado. Um adulto tem de aprovar.'), findsOneWidget);
  });

  testWidgets('um adulto aprova o pedido de uma criança', (tester) async {
    final fake = _FakeApi('mom', 'Admin');
    await _pump(tester, fake);

    expect(find.text('Nova recompensa'), findsOneWidget);
    expect(find.text('Um gelado'), findsOneWidget);

    await tester.tap(find.byTooltip('Aprovar'));
    await tester.pumpAndSettle();

    expect(fake.actions, ['POST /api/households/h1/redemptions/x1/approve']);
    expect(find.text('Aprovado.'), findsOneWidget);
  });
}
