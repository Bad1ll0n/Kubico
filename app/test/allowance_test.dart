import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/allowance_section.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

const _members = [
  MemberPoints(userId: 'mum', displayName: 'Mãe', color: '#4F7CAC', earned: 0, spent: 0, reserved: 0, available: 0),
  MemberPoints(userId: 'kid', displayName: 'Rita', color: '#E07A5F', earned: 9, spent: 0, reserved: 0, available: 9),
];

Map<String, dynamic> _ritaAllowance({bool lastWeekPaid = false}) => {
  'userId': 'kid',
  'amountCents': 500,
  'bonusPerChoreCents': 50,
  'period': 'Weekly',
  'startDate': '2026-10-12',
  'owedCents': lastWeekPaid ? 0 : 700,
  'periods': [
    {
      'start': '2026-10-19',
      'end': '2026-10-25',
      'choresDone': 2,
      'amountCents': 600,
      'isCurrent': true,
      'paymentId': null,
      'paidAt': null,
    },
    {
      'start': '2026-10-12',
      'end': '2026-10-18',
      'choresDone': 4,
      'amountCents': 700,
      'isCurrent': false,
      'paymentId': lastWeekPaid ? 'pay1' : null,
      'paidAt': lastWeekPaid ? '2026-10-19T18:00:00Z' : null,
    },
  ],
};

/// A semana passada foi paga a meio e depois a Rita fez mais duas tarefas (1,00 € por pagar).
Map<String, dynamic> _paidEarly() {
  final allowance = _ritaAllowance(lastWeekPaid: true);
  final periods = [...(allowance['periods'] as List).cast<Map<String, dynamic>>()];
  periods[1] = {...periods[1], 'choresDone': 6, 'owedCents': 100};
  return {...allowance, 'owedCents': 100, 'periods': periods};
}

class _Server {
  _Server({this.allowances = const []});

  List<Map<String, dynamic>> allowances;
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (request.method != 'GET') requests.add('${request.method} $path ${request.body}');
    if (path == '/api/households/h1/allowances') return _json(allowances);
    if (path == '/api/households/h1/allowances/kid' && request.method == 'PUT') {
      allowances = [_ritaAllowance()];
      return _json(allowances.single);
    }
    if (path == '/api/households/h1/allowances/kid/payments') {
      allowances = [_ritaAllowance(lastWeekPaid: true)];
      return _json(allowances.single);
    }
    return http.Response('', 404);
  });
}

Future<void> _pump(WidgetTester tester, _Server server, String role) async {
  tester.view.physicalSize = const Size(900, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
  final household = HouseholdSummary(
    id: 'h1',
    name: 'Casa',
    role: HouseholdRole.fromApi(role),
    access: ModuleAccess.parse(null, HouseholdRole.fromApi(role)),
  );
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        Provider.value(
          value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
        ),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: ListView(
            children: [AllowanceSection(household: household, members: _members)],
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  test('euros escrevem-se e leem-se como em Portugal', () {
    expect(formatEuros(620), '6,20 €');
    expect(formatEuros(5, symbol: false), '0,05');
    expect([parseEuros('5'), parseEuros('5,5'), parseEuros('0,50 €'), parseEuros('7.25')], [500, 550, 50, 725]);
    expect([parseEuros(''), parseEuros('abc'), parseEuros('1,234')], [null, null, null]);
  });

  testWidgets('o administrador dá mesada à Rita e paga a semana que acabou', (tester) async {
    final server = _Server();
    await _pump(tester, server, 'Admin');
    expect(find.textContaining('Ninguém tem mesada'), findsOneWidget);

    await tester.tap(find.text('Dar mesada'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Valor'), '5000');
    await tester.pump();
    expect(find.text('Um valor até 1000 €.'), findsOneWidget);
    expect(tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Guardar')).onPressed, isNull);

    await tester.enterText(find.widgetWithText(TextField, 'Valor'), '5');
    await tester.enterText(find.widgetWithText(TextField, 'Extra por tarefa feita (opcional)'), '0,50');
    await tester.pump();
    // A Mãe também aparece para escolher; a Rita é escolhida na lista.
    await tester.tap(find.byType(DropdownButtonFormField<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Rita').last);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Guardar'));
    await tester.pumpAndSettle();

    expect(
      server.requests.single,
      'PUT /api/households/h1/allowances/kid '
      '{"amountCents":500,"bonusPerChoreCents":50,"period":"Weekly"}',
    );
    expect(find.text('5,00 € por semana + 0,50 € por tarefa feita'), findsOneWidget);
    expect(find.text('Por pagar: 7,00 €'), findsOneWidget);
    expect(find.text('Esta semana'), findsOneWidget);
    expect(find.text('Semana de 12/10 a 18/10'), findsOneWidget);

    // Pagar a semana que ainda não acabou avisa que o que se fizer depois fica por pagar até ao fim da semana.
    await tester.tap(find.widgetWithText(FilledButton, 'Pagar').first);
    await tester.pumpAndSettle();
    expect(find.textContaining('ficam por pagar e aparecem quando esta semana acabar'), findsOneWidget);
    await tester.tap(find.text('Cancelar'));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Pagar').last);
    await tester.pumpAndSettle();
    expect(find.text('Pagar 7,00 € a Rita?'), findsOneWidget);
    expect(find.textContaining('ficam por pagar'), findsNothing);
    await tester.tap(find.widgetWithText(FilledButton, 'Pagar').last);
    await tester.pumpAndSettle();

    expect(server.requests.last, 'POST /api/households/h1/allowances/kid/payments {"periodStart":"2026-10-12"}');
    expect(find.text('Mesada de Rita marcada como paga.'), findsOneWidget);
    expect(find.text('Pago'), findsOneWidget);
    expect(find.textContaining('Por pagar'), findsNothing);
  });

  testWidgets('uma semana paga antes de acabar mostra o que falta e paga-se o resto', (tester) async {
    final server = _Server(allowances: [_paidEarly()]);
    await _pump(tester, server, 'Admin');

    expect(find.textContaining('falta pagar 1,00 €'), findsOneWidget);
    expect(find.text('Por pagar: 1,00 €'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Pagar o resto'));
    await tester.pumpAndSettle();
    expect(find.text('Pagar 1,00 € a Rita?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Pagar').last);
    await tester.pumpAndSettle();

    expect(server.requests.last, 'POST /api/households/h1/allowances/kid/payments {"periodStart":"2026-10-12"}');
  });

  testWidgets('a criança vê a sua mesada, sem pagar nem mudar nada', (tester) async {
    final server = _Server(allowances: [_ritaAllowance(lastWeekPaid: true)]);
    await _pump(tester, server, 'Child');

    expect(find.text('Rita'), findsOneWidget);
    expect(find.textContaining('2 tarefas feitas'), findsOneWidget);
    expect(find.text('Pago'), findsOneWidget);
    expect(find.text('Pagar'), findsNothing);
    expect(find.byTooltip('Mudar a mesada de Rita'), findsNothing);
    expect(find.text('Dar mesada'), findsNothing);
  });

  testWidgets('sem mesada, a criança não vê a secção', (tester) async {
    await _pump(tester, _Server(), 'Child');
    expect(find.text('Mesada'), findsNothing);
  });
}
