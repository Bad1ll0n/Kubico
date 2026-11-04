import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/l10n/strings.dart';
import 'package:kubico/src/l10n/strings_en.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/shopping/voice_input.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/account_screen.dart';
import 'package:kubico/src/ui/app.dart';
import 'package:kubico/src/ui/calendar_view.dart';
import 'package:kubico/src/ui/dates_pt.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Os textos marcados com `.tr` no código (só os que não têm partes variáveis), com o ficheiro onde estão.
Map<String, String> _markedTexts() {
  final literal = RegExp(r"""((?:(?:'(?:[^'\\\n$]|\\.)*'|"(?:[^"\\\n$]|\\.)*")\s*)+)\.tr\b""");
  final part = RegExp(
    r"'((?:[^'\\\n$]|\\.)*)'"
    r'|"((?:[^"\\\n$]|\\.)*)"',
  );
  String unescape(String s) => s.replaceAllMapped(RegExp(r'\\(.)'), (m) => m[1] == 'n' ? '\n' : m[1]!);
  final found = <String, String>{};
  for (final file in Directory('lib').listSync(recursive: true).whereType<File>()) {
    if (!file.path.endsWith('.dart') || file.path.contains('l10n')) continue;
    final source = file.readAsStringSync();
    for (final m in literal.allMatches(source)) {
      // Só o início de uma string: não o fim de uma com partes variáveis ($) nem um pedaço entre interpolações.
      final before = source.substring(0, m.start).trimRight();
      if (before.isNotEmpty && RegExp(r'''[\w'"}$]''').hasMatch(before[before.length - 1])) continue;
      final text = [for (final p in part.allMatches(m[1]!)) unescape(p[1] ?? p[2]!)].join();
      found.putIfAbsent(text, () => file.path);
    }
  }
  return found;
}

void main() {
  tearDown(() => AppLanguage.current.value = AppLanguage.portuguese);

  test('todos os textos marcados para tradução têm tradução para inglês', () {
    final marked = _markedTexts();
    final missing = {
      for (final MapEntry(:key, :value) in marked.entries)
        if (!englishTexts.containsKey(key)) key: value,
    };

    expect(marked.length, greaterThan(350));
    expect(missing, isEmpty);
  });

  test('as traduções usam só as partes variáveis que a frase tem', () {
    final placeholder = RegExp(r'\{\d+\}');
    for (final MapEntry(:key, :value) in englishTexts.entries) {
      final inKey = {for (final m in placeholder.allMatches(key)) m[0]};
      final inValue = {for (final m in placeholder.allMatches(value)) m[0]};
      expect(inKey.containsAll(inValue), isTrue, reason: key);
    }
  });

  test('em inglês, as frases com partes variáveis mantêm nomes e números', () {
    const name = 'Rita';
    const days = 3;
    AppLanguage.current.value = AppLanguage.english;

    expect('Olá, $name!'.tr, 'Hi, Rita!');
    expect('Adicionados $days artigos.'.tr, 'Added 3 items.');
    expect('Guardar'.tr, 'Save');
    expect('Um texto sem tradução'.tr, 'Um texto sem tradução');
    expect(
      describeRecurrence(Recurrence(RecurrenceFrequency.weekly, interval: 2, weekdays: const [2, 4])),
      'Every 2 weeks (Tue, Thu)',
    );
    expect(formatLongDate(DateTime(2026, 10, 20)), 'Tuesday, 20 October');
    expect(HouseholdRole.child.label, 'Child');
    expect(formatEuros(620), '€6.20');
    expect(parseEuros('€6.20'), 620);

    AppLanguage.current.value = AppLanguage.portuguese;
    expect('Olá, $name!'.tr, 'Olá, Rita!');
    expect(formatLongDate(DateTime(2026, 10, 20)), 'Terça-feira, 20 de outubro');
    expect(formatEuros(620), '6,20 €');
  });

  test('o ditado percebe as duas línguas', () {
    expect(parseSpokenItems('leite, pão e duas dúzias de ovos'), ['leite', 'pão', '24 ovos']);
    expect(parseSpokenItems('a milk, bread and two yoghurts'), ['milk', 'bread', '2 yoghurts']);
  });

  test('a língua escolhida fica guardada e vai em cada pedido', () async {
    final store = MemoryKeyValueStore();
    String? sent;
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((request) async {
        sent = request.headers['Accept-Language'];
        return _json({'title': 'Incorrect email or password.'}, 401);
      }),
    );
    await AppLanguage.restore(store);

    await AppLanguage.set('en');
    await expectLater(api.login('a@b.pt', 'x'), throwsA(isA<ApiException>()));
    await AppLanguage.restore(store);

    expect(store.values['kubico.language'], 'en');
    expect(AppLanguage.code, 'en');
    expect(sent, startsWith('en'));
  });

  testWidgets('no ecrã de entrada muda-se para inglês e na conta a língua vai para o servidor', (tester) async {
    String? savedLanguage;
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((request) async {
        if (request.url.path == '/api/account/language') {
          savedLanguage = (jsonDecode(request.body) as Map<String, dynamic>)['language'] as String;
          return _json({'id': 'u1', 'email': 'ana@kubico.test', 'displayName': 'Ana', 'language': savedLanguage});
        }
        return http.Response('', 404);
      }),
    );
    final session = SessionController(api: api, store: MemoryTokenStore())..status = SessionStatus.signedOut;
    await AppLanguage.restore(MemoryKeyValueStore());
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
    expect(find.text('Entrar'), findsWidgets);

    await tester.tap(find.text('English'));
    await tester.pumpAndSettle();

    expect(find.text('Sign in'), findsWidgets);
    expect(find.text('Create a new account'), findsOneWidget);
    final context = tester.element(find.byType(Scaffold).first);
    expect(MaterialLocalizations.of(context).cancelButtonLabel, 'Cancel');

    // Na conta: a língua muda também no servidor (para os avisos).
    session.user = const User(id: 'u1', email: 'ana@kubico.test', displayName: 'Ana');
    await tester.pumpWidget(
      MultiProvider(
        providers: [ChangeNotifierProvider.value(value: session)],
        child: const MaterialApp(home: Scaffold(body: LanguageCard())),
      ),
    );
    expect(find.text('Language'), findsOneWidget);
    await tester.tap(find.text('Português'));
    await tester.pumpAndSettle();

    expect(AppLanguage.code, 'pt');
    expect(savedLanguage, 'pt');
    expect(session.user!.language, 'pt');
  });
}
