import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/first_steps.dart';
import 'package:kubico/src/ui/theme.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

void main() {
  test('criar uma casa pede os primeiros passos (F4-06)', () async {
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient(
        (request) async => switch (request.url.path) {
          '/api/households' => _json({'id': 'h1', 'name': 'Casa', 'role': 'Admin'}, 201),
          '/api/me' => _json({
            'user': {'id': 'u1', 'email': 'mae@kubico.test', 'displayName': 'Mãe', 'isChildProfile': false},
            'households': [
              {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
            ],
          }),
          _ => http.Response('', 404),
        },
      ),
    )..accessToken = 't';
    final session = SessionController(api: api, store: MemoryTokenStore());

    await session.createHousehold('Casa');

    expect(session.justCreated.value, 'h1');
  });

  testWidgets('os primeiros passos levam a convidar e acabam em "Começar"', (tester) async {
    tester.view.physicalSize = const Size(600, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(Brightness.light),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () =>
                Navigator.of(context)
                    .push(MaterialPageRoute<void>(builder: (_) => const FirstStepsScreen(householdId: 'h1'))),
            child: const Text('abrir'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('abrir'));
    await tester.pumpAndSettle();

    expect(find.text('Convida a família'), findsOneWidget);
    expect(find.text('Cria os perfis das crianças'), findsOneWidget);
    expect(find.text('Põe a app no ecrã inicial'), findsOneWidget);
    await tester.ensureVisible(find.text('Começar'));
    await tester.tap(find.text('Começar'));
    await tester.pumpAndSettle();
    expect(find.text('abrir'), findsOneWidget);
  });

  testWidgets('uma dica de primeira vez sai com "Percebi" e não volta neste aparelho', (tester) async {
    final store = MemoryKeyValueStore();
    await Tips.restore(store);
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(Brightness.light),
        home: const Scaffold(
          body: FirstTimeTip(id: 'shopping', text: 'Desliza para marcar'),
        ),
      ),
    );

    expect(find.text('Desliza para marcar'), findsOneWidget);
    await tester.tap(find.text('Percebi'));
    await tester.pumpAndSettle();
    expect(find.text('Desliza para marcar'), findsNothing);

    await Tips.restore(store);
    expect(Tips.seen.value, contains('shopping'));
  });
}
