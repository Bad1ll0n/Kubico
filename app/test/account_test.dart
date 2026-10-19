import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/account_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

class _Server {
  _Server({this.child = false});

  final bool child;
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (path == '/api/me') {
      return _json({
        'user': {
          'id': 'mum',
          'email': child ? null : 'mae@kubico.test',
          'displayName': child ? 'Rita' : 'Mãe',
          'isChildProfile': child,
        },
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': child ? 'Child' : 'Admin'},
        ],
      });
    }
    requests.add('${request.method} $path ${request.body}');
    if (path == '/api/account/export') {
      return http.Response.bytes(
        utf8.encode('{"Conta":{"Nome":"Mãe"}}'),
        200,
        headers: {
          'content-type': 'application/json',
          'content-disposition':
              'attachment; filename=kubico-dados-2026-10-19.json; filename*=UTF-8\'\'kubico-dados-2026-10-19.json',
        },
      );
    }
    if (path == '/api/account' && request.method == 'DELETE') {
      final password = (jsonDecode(request.body) as Map<String, dynamic>)['password'];
      return password == 'certa' ? http.Response('', 204) : _json({'title': 'A palavra-passe não está certa.'}, 400);
    }
    return http.Response('', 404);
  });
}

Future<SessionController> _pump(
  WidgetTester tester,
  _Server server, {
  Future<String?> Function(String, Uint8List)? save,
  Future<void> Function()? onDeleted,
}) async {
  tester.view.physicalSize = const Size(900, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
  final session = SessionController(api: api, store: MemoryTokenStore());
  await session.refresh();
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
      ],
      child: MaterialApp(
        home: Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) => AccountScreen(saveFile: save ?? (_, _) async => null, onDeleted: onDeleted),
                ),
              ),
              child: const Text('Abrir a conta'),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.tap(find.text('Abrir a conta'));
  await tester.pumpAndSettle();
  return session;
}

void main() {
  testWidgets('descarregar os dados guarda o ficheiro com o nome que o servidor sugere', (tester) async {
    final server = _Server();
    String? savedName;
    Uint8List? savedBytes;
    await _pump(
      tester,
      server,
      save: (name, bytes) async {
        (savedName, savedBytes) = (name, bytes);
        return '/Transferências/$name';
      },
    );
    expect(find.text('mae@kubico.test'), findsOneWidget);

    await tester.tap(find.text('Descarregar os meus dados'));
    await tester.pumpAndSettle();

    expect(savedName, 'kubico-dados-2026-10-19.json');
    expect(utf8.decode(savedBytes!), contains('"Nome":"Mãe"'));
    expect(find.text('Os teus dados foram guardados.'), findsOneWidget);
  });

  testWidgets('apagar a conta pede a palavra-passe, arruma a app e volta ao início sem sessão', (tester) async {
    final server = _Server();
    var cleaned = false;
    final session = await _pump(tester, server, onDeleted: () async => cleaned = true);

    await tester.tap(find.text('Apagar a minha conta'));
    await tester.pumpAndSettle();
    expect(find.text('Apagar a tua conta?'), findsOneWidget);
    final confirm = find.widgetWithText(FilledButton, 'Apagar a conta');
    expect(tester.widget<FilledButton>(confirm).onPressed, isNull);

    await tester.enterText(find.widgetWithText(TextField, 'Palavra-passe'), 'errada');
    await tester.pump();
    await tester.tap(confirm);
    await tester.pumpAndSettle();
    expect(find.text('A palavra-passe não está certa.'), findsOneWidget);
    expect(session.status, SessionStatus.signedIn);

    await tester.tap(find.text('Apagar a minha conta'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Palavra-passe'), 'certa');
    await tester.pump();
    await tester.tap(find.widgetWithText(FilledButton, 'Apagar a conta'));
    await tester.pumpAndSettle();

    expect(server.requests.last, 'DELETE /api/account {"password":"certa"}');
    expect(cleaned, isTrue);
    expect(session.status, SessionStatus.signedOut);
    expect(find.text('Abrir a conta'), findsOneWidget);
    expect(find.text('A tua conta foi apagada.'), findsOneWidget);
  });

  testWidgets('um perfil de criança não apaga a conta', (tester) async {
    await _pump(tester, _Server(child: true));

    expect(find.textContaining('pede-lhes para o apagar'), findsOneWidget);
    expect(find.text('Apagar a minha conta'), findsNothing);
    expect(find.text('Descarregar os meus dados'), findsOneWidget);
  });
}
