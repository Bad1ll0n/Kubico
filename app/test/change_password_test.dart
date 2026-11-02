import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/ui/change_password.dart';
import 'package:provider/provider.dart';

void main() {
  testWidgets('muda-se a palavra-passe com a atual; a nova tem de se repetir igual', (tester) async {
    final sent = <Map<String, dynamic>>[];
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((request) async {
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        sent.add(body);
        return body['currentPassword'] == 'antiga-antiga'
            ? http.Response('', 204)
            : http.Response.bytes(
                utf8.encode(jsonEncode({'title': 'A palavra-passe não está certa.'})),
                400,
                headers: {'content-type': 'application/problem+json; charset=utf-8'},
              );
      }),
    )..accessToken = 't';
    await tester.pumpWidget(
      Provider.value(
        value: api,
        child: const MaterialApp(home: Scaffold(body: ChangePasswordCard())),
      ),
    );

    await tester.tap(find.text('Mudar a palavra-passe'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextFormField, 'Palavra-passe atual'), 'errada-errada');
    await tester.enterText(find.widgetWithText(TextFormField, 'Nova palavra-passe'), 'nova-palavra');
    await tester.enterText(find.widgetWithText(TextFormField, 'Repete a nova palavra-passe'), 'outra-coisa');
    await tester.tap(find.text('Mudar'));
    await tester.pumpAndSettle();
    expect(find.text('As palavras-passe não são iguais.'), findsOneWidget);
    expect(sent, isEmpty);

    await tester.enterText(find.widgetWithText(TextFormField, 'Repete a nova palavra-passe'), 'nova-palavra');
    await tester.tap(find.text('Mudar'));
    await tester.pumpAndSettle();
    expect(find.text('A palavra-passe não está certa.'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextFormField, 'Palavra-passe atual'), 'antiga-antiga');
    await tester.tap(find.text('Mudar'));
    await tester.pumpAndSettle();

    expect(sent.last, {'currentPassword': 'antiga-antiga', 'newPassword': 'nova-palavra'});
    expect(find.text('Palavra-passe mudada.'), findsOneWidget);
  });
}
