import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/ui/smart_home_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

void main() {
  testWidgets('liga-se o Home Assistant e cria-se a regra da máquina de lavar (F4-16c)', (tester) async {
    tester.view.physicalSize = const Size(700, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    var connected = false;
    final rules = <Map<String, dynamic>>[];
    final sent = <String>[];
    final client = MockClient((request) async {
      final path = request.url.path;
      if (request.method != 'GET') sent.add('${request.method} $path ${request.body}');
      Map<String, dynamic> home() => {
        'connected': connected,
        'baseUrl': connected ? 'http://192.168.1.50:8123/' : null,
        'lastCheckedAt': null,
        'lastError': null,
        'rules': rules,
      };
      return switch ((request.method, path)) {
        ('GET', '/api/households/h1/smart-home') => _json(home()),
        ('PUT', '/api/households/h1/smart-home/connection') => () {
          connected = true;
          return _json(home());
        }(),
        ('GET', '/api/households/h1/smart-home/entities') => _json([
          {'entityId': 'sensor.maquina', 'state': 'a lavar', 'name': 'Máquina de lavar'},
        ]),
        ('POST', '/api/households/h1/smart-home/rules') => () {
          rules.add({
            ...(jsonDecode(request.body) as Map<String, dynamic>),
            'id': 'r1',
            'lastState': 'a lavar',
            'lastTriggeredAt': null,
          });
          return _json(rules.last, 201);
        }(),
        _ => http.Response('', 404),
      };
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
    const members = [Member(userId: 'pai', displayName: 'Pai', role: HouseholdRole.adult, color: '#2E7550')];

    await tester.pumpWidget(
      Provider.value(
        value: api,
        child: const MaterialApp(
          home: Scaffold(
            body: SmartHomeView(householdId: 'h1', members: members),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.enterText(find.widgetWithText(TextField, 'Endereço'), 'http://192.168.1.50:8123');
    await tester.enterText(find.widgetWithText(TextField, 'Token de longa duração'), 'segredo-longo');
    await tester.tap(find.text('Ligar'));
    await tester.pumpAndSettle();

    expect(sent.single, contains('"token":"segredo-longo"'));
    expect(find.text('http://192.168.1.50:8123/'), findsOneWidget);
    expect(find.text('Ainda não há regras'), findsOneWidget);
    // O token não fica no ecrã depois de ligar.
    expect(find.text('segredo-longo'), findsNothing);

    await tester.tap(find.text('Nova regra'));
    await tester.pumpAndSettle();
    expect(find.text('Agora está «a lavar»'), findsOneWidget);
    await tester.enterText(find.widgetWithText(TextField, 'ficar neste estado'), 'terminado');
    await tester.enterText(find.widgetWithText(TextField, 'criar a tarefa'), 'Estender a roupa');
    await tester.tap(find.text('Ninguém em especial'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Pai').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    final rule = jsonDecode(sent.last.split(' ').skip(2).join(' ')) as Map<String, dynamic>;
    expect(rule, {
      'entityId': 'sensor.maquina',
      'entityName': 'Máquina de lavar',
      'toState': 'terminado',
      'choreTitle': 'Estender a roupa',
      'assigneeUserId': 'pai',
      'points': 0,
      'enabled': true,
    });
    expect(find.text('Estender a roupa'), findsOneWidget);
    expect(find.text('Quando «Máquina de lavar» ficar «terminado» · Pai'), findsOneWidget);
  });
}
