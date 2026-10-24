import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/shopping/voice_input.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/shopping_items_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

const _items = '/api/households/h1/lists/l1/items';

/// Servidor com a lista "l1" e uma base de produtos ([products]: código → produto; sem código, 404).
class _Server {
  _Server({this.products = const {}});

  final Map<String, Map<String, dynamic>> products;
  final items = <String, Map<String, dynamic>>{};
  final added = <Map<String, dynamic>>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (path.startsWith('/api/households/h1/products/')) {
      final product = products[path.split('/').last];
      return product == null
          ? _json({'title': 'Não conhecemos este produto. Escreve o nome: fica guardado para a próxima vez.'}, 404)
          : _json(product);
    }
    if (path == _items && request.method == 'GET') return _json(items.values.toList());
    if (path == _items && request.method == 'POST') {
      final body = jsonDecode(request.body) as Map<String, dynamic>;
      added.add(body);
      final item = items[body['id'] as String] = {
        'id': body['id'],
        'listId': 'l1',
        'name': body['name'],
        'quantity': body['quantity'],
        'category': body['category'],
        'isChecked': false,
        'addedByUserId': 'u1',
        'createdAt': '2026-10-19T10:00:00Z',
        'updatedAt': '2026-10-19T10:00:00Z',
        'barcode': body['barcode'],
      };
      return _json(item, 201);
    }
    return http.Response('', 404);
  });
}

/// Ditado de faz de conta: diz o que lhe mandarem, ou não está disponível.
class _FakeVoice implements VoiceInput {
  _FakeVoice(this.said, {this.available = true, this.error});

  final String said;
  final bool available;

  /// O erro com que o reconhecimento pára (como o do sistema), em vez de ouvir.
  final String? error;
  var stopped = false;

  @override
  Future<bool> start({required void Function(String, bool) onText, required void Function(String?) onDone}) async {
    if (!available) return false;
    if (error != null) {
      onDone(describeSpeechError(error!));
      return true;
    }
    onText(said.split(' ').first, false);
    onText(said, true);
    onDone(null);
    return true;
  }

  @override
  Future<void> stop() async => stopped = true;
}

Future<void> _pump(WidgetTester tester, _Server server, {String? scanned, VoiceInput? voice}) async {
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
  final shopping = ShoppingRepository(
    api: api,
    store: MemoryKeyValueStore(),
    retryEvery: const Duration(hours: 1),
    saveDelay: Duration.zero,
  );
  const list = ShoppingList(id: 'l1', householdId: 'h1', name: 'Supermercado', itemCount: 0, checkedCount: 0);
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: shopping),
        Provider.value(
          value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
        ),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: ShoppingItemsView(householdId: 'h1', list: list, voice: voice, scanBarcode: (_) async => scanned),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  test('o que se dita passa a artigos, com os números por extenso em algarismos', () {
    expect(parseSpokenItems('leite, pão e dois iogurtes'), ['leite', 'pão', '2 iogurtes']);
    expect(parseSpokenItems('Uma dúzia de ovos mais um pacote de arroz'), ['12 ovos', 'pacote de arroz']);
    expect(parseSpokenItems('1,5 kg de batatas; três limões.'), ['1,5 kg de batatas', '3 limões']);
    expect(parseSpokenItems('  '), isEmpty);
  });

  test('só servem códigos de barras com o dígito de controlo certo', () {
    expect(isValidBarcode('5449000000996'), isTrue);
    expect(isValidBarcode('96385074'), isTrue);
    expect(isValidBarcode('5449000000997'), isFalse);
    expect(isValidBarcode('https://kubico.pt'), isFalse);
  });

  testWidgets('ler um produto conhecido propõe o nome e a secção, e põe-no na lista com o código', (tester) async {
    final server = _Server(
      products: {
        '5449000000996': {
          'barcode': '5449000000996',
          'name': 'Coca-Cola 330 ml',
          'category': 'Bebidas',
          'brand': 'Coca-Cola',
          'size': '330 ml',
          'fromHousehold': false,
        },
      },
    );
    await _pump(tester, server, scanned: '5449000000996');

    await tester.tap(find.byTooltip('Ler código de barras'));
    await tester.pumpAndSettle();
    expect(find.text('Adicionar à lista'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'Coca-Cola 330 ml'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'Bebidas'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Adicionar'));
    await tester.pumpAndSettle();
    expect(find.text('Coca-Cola 330 ml'), findsOneWidget);
    expect(server.added.single, containsPair('barcode', '5449000000996'));
    expect(server.added.single, containsPair('category', 'Bebidas'));

    // O mesmo código outra vez: já está na lista.
    await tester.tap(find.byTooltip('Ler código de barras'));
    await tester.pumpAndSettle();
    expect(find.text('«Coca-Cola 330 ml» já está na lista.'), findsOneWidget);
    expect(server.added, hasLength(1));
  });

  testWidgets('um produto que não conhecemos pede o nome antes de o pôr na lista', (tester) async {
    final server = _Server();
    await _pump(tester, server, scanned: '5000112548167');

    await tester.tap(find.byTooltip('Ler código de barras'));
    await tester.pumpAndSettle();
    expect(find.text('Não conhecemos este produto. Escreve o nome: fica guardado para a próxima vez.'), findsOneWidget);
    final add = find.widgetWithText(FilledButton, 'Adicionar');
    expect(tester.widget<FilledButton>(add).onPressed, isNull);

    await tester.enterText(find.widgetWithText(TextField, 'Nome'), 'Bolachas da avó');
    await tester.pump();
    await tester.tap(add);
    await tester.pumpAndSettle();

    expect(find.text('Bolachas da avó'), findsOneWidget);
    expect(server.added.single, containsPair('barcode', '5000112548167'));
  });

  testWidgets('os artigos ditados entram todos na lista', (tester) async {
    final server = _Server();
    final voice = _FakeVoice('leite, pão e dois iogurtes');
    await _pump(tester, server, voice: voice);

    await tester.tap(find.byTooltip('Ditar artigos'));
    await tester.pumpAndSettle();
    expect(find.text('Corrige o que for preciso e adiciona.'), findsOneWidget);
    expect(find.widgetWithText(Chip, '2 iogurtes'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Adicionar 3'));
    await tester.pumpAndSettle();

    expect(voice.stopped, isTrue);
    expect(find.text('Adicionados 3 artigos.'), findsOneWidget);
    expect(find.text('iogurtes'), findsOneWidget);
    expect([for (final a in server.added) a['name']], ['leite', 'pão', 'iogurtes']);
    expect(server.added.last, containsPair('quantity', '2'));
  });

  testWidgets('sem autorização para o microfone, a janela diz porquê', (tester) async {
    await _pump(tester, _Server(), voice: _FakeVoice('', error: 'not-allowed'));

    await tester.tap(find.byTooltip('Ditar artigos'));
    await tester.pumpAndSettle();

    expect(find.textContaining('não tem autorização para usar o microfone'), findsOneWidget);
    expect(find.text('Não se percebeu nenhum artigo.'), findsNothing);
    expect(describeSpeechError('error_no_match'), 'Não se ouviu nada. Toca no microfone para tentar outra vez.');
  });

  testWidgets('sem ditado no dispositivo, escrevem-se os artigos na mesma janela', (tester) async {
    final server = _Server();
    await _pump(tester, server, voice: _FakeVoice('', available: false));

    await tester.tap(find.byTooltip('Ditar artigos'));
    await tester.pumpAndSettle();
    expect(find.textContaining('O ditado não está disponível neste dispositivo'), findsOneWidget);

    await tester.enterText(find.byType(TextField).last, 'café');
    await tester.pump();
    await tester.tap(find.widgetWithText(FilledButton, 'Adicionar'));
    await tester.pumpAndSettle();

    expect(find.text('Adicionado: café.'), findsOneWidget);
    expect(server.added.single, containsPair('name', 'café'));
  });
}
