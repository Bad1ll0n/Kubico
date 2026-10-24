import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/help_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Future<List<Map<String, dynamic>>> _pump(WidgetTester tester, {required bool support}) async {
  tester.view.physicalSize = const Size(900, 2000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final sent = <Map<String, dynamic>>[];
  final server = MockClient((request) async {
    switch ((request.method, request.url.path)) {
      case ('GET', '/api/me'):
        return _json({
          'user': {'id': 'u1', 'email': 'mae@kubico.test', 'displayName': 'Mãe', 'isChildProfile': false},
          'households': [
            {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
          ],
          'isSupport': support,
        });
      case ('POST', '/api/feedback'):
        sent.add(jsonDecode(request.body) as Map<String, dynamic>);
        return http.Response('', 204);
      case ('GET', '/api/feedback'):
        return _json([
          {
            'id': 'f1',
            'kind': 'Idea',
            'message': 'Era bom ter receitas.',
            'device': 'android',
            'userName': 'Pai',
            'userEmail': 'pai@kubico.test',
            'createdAt': '2026-10-18T09:30:00Z',
          },
        ]);
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
      ],
      child: const MaterialApp(home: HelpScreen(householdId: 'h1')),
    ),
  );
  await tester.pumpAndSettle();
  return sent;
}

void main() {
  testWidgets('as perguntas frequentes abrem a resposta e um problema chega ao servidor', (tester) async {
    final sent = await _pump(tester, support: false);

    await tester.tap(find.text('O que acontece quando alguém vai de férias?'));
    await tester.pumpAndSettle();
    expect(find.textContaining('as rotações saltam essa pessoa'), findsOneWidget);
    expect(find.text('Mensagens recebidas'), findsNothing);

    await tester.tap(find.text('Reportar um problema'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Enviar'));
    await tester.pumpAndSettle();
    expect(find.text('Escreve um pouco mais.'), findsOneWidget);

    await tester.enterText(find.byType(TextFormField), 'A lista não atualiza no telemóvel.');
    await tester.tap(find.text('Enviar'));
    await tester.pumpAndSettle();

    expect(sent.single, {'kind': 'Problem', 'message': 'A lista não atualiza no telemóvel.', 'householdId': 'h1'});
    expect(find.text('Obrigado! A mensagem foi enviada.'), findsOneWidget);
  });

  testWidgets('quem mantém o Kubico vê as mensagens recebidas', (tester) async {
    await _pump(tester, support: true);

    await tester.tap(find.text('Mensagens recebidas'));
    await tester.pumpAndSettle();

    expect(find.text('Era bom ter receitas.'), findsOneWidget);
    expect(find.textContaining('Pai · android'), findsOneWidget);
  });
}
