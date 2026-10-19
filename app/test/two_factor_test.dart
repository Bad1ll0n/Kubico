import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/auth_screen.dart';
import 'package:kubico/src/ui/two_factor_section.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

const _user = {'id': 'u1', 'email': 'mae@kubico.test', 'displayName': 'Mãe'};
const _codes = ['ABCD-EFGH', 'JKMN-PQRS', 'TUVW-XYZ2', '3456-789A', 'BCDE-FGHJ', 'KMNP-QRST', 'UVWX-YZ23', '4567-89AB'];

/// Servidor com verificação em dois passos: o código certo é 123456; o de recuperação, ABCD-EFGH.
class _Server {
  var enabled = false;
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    final body = request.body.isEmpty ? const <String, dynamic>{} : jsonDecode(request.body) as Map<String, dynamic>;
    requests.add('${request.method} $path');
    switch (path) {
      case '/api/auth/login':
        return _json({'twoFactorRequired': true, 'challengeToken': 'bilhete'});
      case '/api/auth/login/2fa':
        final ok = body['challengeToken'] == 'bilhete' && (body['code'] == '123456' || body['code'] == 'ABCD-EFGH');
        return ok
            ? _json({'accessToken': 'token-1', 'expiresAt': '2030-01-01T00:00:00Z', 'user': _user})
            : _json({'title': 'O código não está certo.'}, 401);
      case '/api/me':
        return _json({'user': _user, 'households': <Object>[]});
      case '/api/account/2fa':
        return _json({'enabled': enabled, 'recoveryCodesLeft': enabled ? 8 : 0});
      case '/api/account/2fa/setup':
        return _json({
          'secret': 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP',
          'uri': 'otpauth://totp/Kubico:mae@kubico.test?secret=X',
        });
      case '/api/account/2fa/enable':
        if (body['code'] != '123456') {
          return _json({'title': 'O código não está certo. Confirma que o telemóvel está com a hora certa.'}, 400);
        }
        enabled = true;
        return _json({'recoveryCodes': _codes});
      case '/api/account/2fa/disable':
        enabled = false;
        return http.Response('', 204);
    }
    return http.Response('', 404);
  });
}

Future<(SessionController, ApiClient)> _pump(WidgetTester tester, _Server server, Widget Function() screen) async {
  tester.view.physicalSize = const Size(900, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client);
  final session = SessionController(api: api, store: MemoryTokenStore());
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
      ],
      child: MaterialApp(home: Scaffold(body: screen())),
    ),
  );
  await tester.pumpAndSettle();
  return (session, api);
}

void main() {
  testWidgets('com verificação em dois passos, a entrada pede o código depois da palavra-passe', (tester) async {
    final server = _Server();
    final (session, _) = await _pump(tester, server, () => const AuthScreen());

    await tester.enterText(find.widgetWithText(TextFormField, 'Email'), 'mae@kubico.test');
    await tester.enterText(find.widgetWithText(TextFormField, 'Palavra-passe'), 'certa-certa');
    await tester.tap(find.widgetWithText(FilledButton, 'Entrar'));
    await tester.pumpAndSettle();

    expect(find.text('Verificação em dois passos'), findsOneWidget);
    expect(session.status, isNot(SessionStatus.signedIn));

    await tester.enterText(find.widgetWithText(TextField, 'Código'), '000000');
    await tester.tap(find.widgetWithText(FilledButton, 'Entrar'));
    await tester.pumpAndSettle();
    expect(find.text('O código não está certo.'), findsOneWidget);

    // Perdeu o telemóvel: entra com um código de recuperação.
    await tester.tap(find.text('Perdi o telemóvel: usar um código de recuperação'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Código de recuperação'), 'ABCD-EFGH');
    await tester.tap(find.widgetWithText(FilledButton, 'Entrar'));
    await tester.pumpAndSettle();

    expect(session.status, SessionStatus.signedIn);
    expect(server.requests.where((r) => r == 'POST /api/auth/login/2fa'), hasLength(2));
  });

  testWidgets('voltar do código regressa à palavra-passe', (tester) async {
    await _pump(tester, _Server(), () => const AuthScreen());
    await tester.enterText(find.widgetWithText(TextFormField, 'Email'), 'mae@kubico.test');
    await tester.enterText(find.widgetWithText(TextFormField, 'Palavra-passe'), 'certa-certa');
    await tester.tap(find.widgetWithText(FilledButton, 'Entrar'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Voltar'));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(TextFormField, 'Palavra-passe'), findsOneWidget);
    expect(find.text('Verificação em dois passos'), findsNothing);
  });

  testWidgets('na conta liga-se com o código da app, mostram-se os códigos de recuperação e desliga-se', (
    tester,
  ) async {
    final server = _Server();
    final (_, api) = await _pump(tester, server, () => const SingleChildScrollView(child: TwoFactorSection()));
    api.accessToken = 't';
    expect(find.text('Ligar'), findsOneWidget);

    await tester.tap(find.text('Ligar'));
    await tester.pumpAndSettle();
    expect(find.text('JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextField, 'Código'), '999999');
    await tester.pump();
    await tester.tap(find.widgetWithText(FilledButton, 'Confirmar'));
    await tester.pumpAndSettle();
    expect(find.textContaining('O código não está certo'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextField, 'Código'), '123456');
    await tester.pump();
    await tester.tap(find.widgetWithText(FilledButton, 'Confirmar'));
    await tester.pumpAndSettle();
    expect(find.text('Guarda os códigos de recuperação'), findsOneWidget);
    expect(find.text('KMNP-QRST'), findsOneWidget);

    await tester.tap(find.text('Já guardei'));
    await tester.pumpAndSettle();
    expect(find.text('Ligada'), findsOneWidget);
    expect(find.textContaining('Tens 8 códigos de recuperação'), findsOneWidget);

    await tester.tap(find.text('Desligar'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Palavra-passe'), 'certa-certa');
    await tester.enterText(find.widgetWithText(TextField, 'Código da app ou de recuperação'), '123456');
    await tester.pump();
    await tester.tap(find.widgetWithText(FilledButton, 'Desligar'));
    await tester.pumpAndSettle();

    expect(server.requests, contains('POST /api/account/2fa/disable'));
    expect(find.text('Verificação em dois passos desligada.'), findsOneWidget);
    expect(find.text('Ligar'), findsOneWidget);
  });
}
