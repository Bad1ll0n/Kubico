import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/app.dart';
import 'package:kubico/src/ui/shopping_items_view.dart';
import 'package:provider/provider.dart';

const _user = {'id': 'u1', 'email': 'filipe@kubico.test', 'displayName': 'Filipe'};

/// Servidor falso com as rotas mínimas para os testes.
http.Response _json(Object body, int status) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

MockClient _fakeServer({List<Map<String, dynamic>> households = const []}) => MockClient((request) async {
  final path = request.url.path;
  if (path == '/api/auth/login') {
    final body = jsonDecode(request.body) as Map<String, dynamic>;
    if (body['password'] != 'certa-certa') {
      return _json({'title': 'Email ou palavra-passe incorretos.'}, 401);
    }
    return _json({'accessToken': 'token-1', 'expiresAt': '2030-01-01T00:00:00Z', 'user': _user}, 200);
  }
  if (path == '/api/me') {
    if (request.headers['Authorization'] != 'Bearer token-1') return http.Response('', 401);
    return _json({'user': _user, 'households': households}, 200);
  }
  if (path == '/api/auth/register') {
    return _json({
      'errors': {
        'Password': ['A palavra-passe é demasiado curta.'],
      },
    }, 400);
  }
  return http.Response('', 404);
});

void main() {
  group('parseItemInput', () {
    test('separa a quantidade do nome', () {
      expect(parseItemInput('2 leite'), ('leite', '2'));
      expect(parseItemInput('1kg arroz'), ('arroz', '1kg'));
      expect(parseItemInput('1,5 L sumo'), ('sumo', '1,5 L'));
    });

    test('sem quantidade devolve só o nome', () {
      expect(parseItemInput('  pão de forma '), ('pão de forma', null));
      expect(parseItemInput('7up'), ('7up', null));
    });
  });

  group('ApiClient', () {
    test('mostra a primeira mensagem de validação', () async {
      final api = ApiClient(baseUrl: 'http://api', httpClient: _fakeServer());

      await expectLater(
        api.register('a@b.pt', 'curta', 'Ana'),
        throwsA(isA<ApiException>().having((e) => e.message, 'message', 'A palavra-passe é demasiado curta.')),
      );
    });
  });

  group('SessionController', () {
    test('entrar guarda o token e carrega as casas', () async {
      final store = MemoryTokenStore();
      final session = SessionController(
        api: ApiClient(
          baseUrl: 'http://api',
          httpClient: _fakeServer(
            households: [
              {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
            ],
          ),
        ),
        store: store,
      );

      await session.signIn('filipe@kubico.test', 'certa-certa');

      expect(session.status, SessionStatus.signedIn);
      expect(session.currentHousehold?.name, 'Casa');
      expect(session.currentHousehold?.role, HouseholdRole.admin);
      expect(store.values['kubico.accessToken'], 'token-1');

      await session.signOut();
      expect(session.status, SessionStatus.signedOut);
      expect(store.values.containsKey('kubico.accessToken'), isFalse);
    });

    test('token guardado inválido termina a sessão', () async {
      final store = MemoryTokenStore()..values['kubico.accessToken'] = 'expirado';
      final session = SessionController(
        api: ApiClient(baseUrl: 'http://api', httpClient: _fakeServer()),
        store: store,
      );

      await session.restore();

      expect(session.status, SessionStatus.signedOut);
      expect(store.values.containsKey('kubico.accessToken'), isFalse);
    });
  });

  testWidgets('ecrã de entrada mostra erro com palavra-passe errada e alterna para registo', (tester) async {
    final api = ApiClient(baseUrl: 'http://api', httpClient: _fakeServer());
    final session = SessionController(api: api, store: MemoryTokenStore());
    await session.restore();

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => ''),
          ),
        ],
        child: const KubicoApp(),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Kubico'), findsOneWidget);
    await tester.enterText(find.widgetWithText(TextFormField, 'Email'), 'filipe@kubico.test');
    await tester.enterText(find.widgetWithText(TextFormField, 'Palavra-passe'), 'errada-errada');
    await tester.tap(find.widgetWithText(FilledButton, 'Entrar'));
    await tester.pumpAndSettle();
    expect(find.text('Email ou palavra-passe incorretos.'), findsOneWidget);

    await tester.tap(find.text('Criar uma conta nova'));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(TextFormField, 'Nome'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Criar conta'), findsOneWidget);
  });

  testWidgets('a app fala português de Portugal (botões e relógio de 24 horas)', (tester) async {
    final api = ApiClient(baseUrl: 'http://api', httpClient: _fakeServer());
    final session = SessionController(api: api, store: MemoryTokenStore());
    await session.restore();

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => ''),
          ),
        ],
        child: const KubicoApp(),
      ),
    );
    await tester.pumpAndSettle();

    final context = tester.element(find.byType(Scaffold).first);
    final localizations = MaterialLocalizations.of(context);
    expect(Localizations.localeOf(context), kubicoLocale);
    expect(localizations.cancelButtonLabel, 'Cancelar');
    expect(localizations.formatTimeOfDay(const TimeOfDay(hour: 18, minute: 5)), '18:05');
  });
}
