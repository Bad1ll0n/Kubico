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
import 'package:kubico/src/ui/chores_view.dart';
import 'package:kubico/src/ui/permissions_screen.dart';
import 'package:kubico/src/ui/shopping_items_view.dart';
import 'package:kubico/src/shopping/shopping_repository.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, String> _access(String shopping, String chores, String calendar, String rewards) => {
  'Shopping': shopping,
  'Chores': chores,
  'Calendar': calendar,
  'Rewards': rewards,
};

final _defaults = [
  {'role': 'Adult', 'access': _access('Manage', 'Manage', 'Manage', 'Manage')},
  {'role': 'Child', 'access': _access('Participate', 'Participate', 'View', 'Participate')},
  {'role': 'Guest', 'access': _access('Participate', 'Participate', 'View', 'Participate')},
];

void main() {
  test('o acesso às áreas vem do servidor; sem ele, usa-se o habitual do papel', () {
    final guest = HouseholdSummary.fromJson({
      'id': 'h1',
      'name': 'Casa',
      'role': 'Guest',
      'access': _access('None', 'View', 'View', 'Participate'),
    });
    final oldServer = HouseholdSummary.fromJson({'id': 'h1', 'name': 'Casa', 'role': 'Child'});

    expect(guest.accessTo(HouseholdModule.shopping), ModuleAccess.none);
    expect(guest.accessTo(HouseholdModule.shopping).canView, isFalse);
    expect(guest.accessTo(HouseholdModule.chores).canParticipate, isFalse);
    expect(guest.accessTo(HouseholdModule.rewards).canParticipate, isTrue);
    expect(oldServer.accessTo(HouseholdModule.calendar), ModuleAccess.view);
    expect(oldServer.accessTo(HouseholdModule.shopping), ModuleAccess.participate);
    expect(HouseholdModule.calendar.levels, isNot(contains(ModuleAccess.participate)));
  });

  testWidgets('o administrador escolhe o que os convidados veem e guarda', (tester) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    final bodies = <Map<String, dynamic>>[];
    List<Map<String, dynamic>> roles = [
      for (final r in _defaults) {...r},
    ];
    final client = MockClient((request) async {
      if (request.url.path != '/api/households/h1/permissions') return http.Response('', 404);
      if (request.method == 'PUT') {
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        bodies.add(body);
        roles = [for (final r in body['roles'] as List) (r as Map).cast<String, dynamic>()];
      }
      return _json({'roles': roles, 'defaults': _defaults});
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';

    await tester.pumpWidget(
      Provider.value(
        value: api,
        child: const MaterialApp(home: PermissionsScreen(householdId: 'h1')),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Convidados'), findsOneWidget);
    expect(find.text('Repor o habitual'), findsNothing);
    final save = find.widgetWithText(FilledButton, 'Guardar');
    expect(tester.widget<FilledButton>(save).onPressed, isNull, reason: 'ainda não mudou nada');

    // Os convidados deixam de ver as compras.
    await tester.tap(find.byKey(const ValueKey('Guest-Shopping')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Não aparece').last);
    await tester.pumpAndSettle();
    expect(find.text('Repor o habitual'), findsOneWidget);

    // No calendário não há "Usa": abrir a lista não acrescenta nenhum (os que se veem são os já escolhidos).
    final chosenUsa = find.text('Usa').evaluate().length;
    await tester.tap(find.byKey(const ValueKey('Child-Calendar')));
    await tester.pumpAndSettle();
    expect(find.text('Usa').evaluate().length, chosenUsa);
    await tester.tap(find.text('Gere tudo').last);
    await tester.pumpAndSettle();

    await tester.tap(save);
    await tester.pumpAndSettle();

    final sent = {for (final r in bodies.single['roles'] as List) (r as Map)['role']: r['access']};
    expect(sent['Guest'], _access('None', 'Participate', 'View', 'Participate'));
    expect(sent['Child'], _access('Participate', 'Participate', 'Manage', 'Participate'));
    expect(find.text('Permissões guardadas.'), findsOneWidget);
    expect(tester.widget<FilledButton>(save).onPressed, isNull);

    // Repor o habitual nos convidados volta a deixar guardar.
    await tester.ensureVisible(find.text('Repor o habitual').last);
    await tester.tap(find.text('Repor o habitual').last);
    await tester.pumpAndSettle();
    expect(tester.widget<FilledButton>(save).onPressed, isNotNull);
  });

  testWidgets('quem só vê as tarefas não as conclui nem cria', (tester) async {
    final client = MockClient((request) async {
      final path = request.url.path;
      if (path == '/api/me') {
        return _json({
          'user': {'id': 'avo', 'email': 'avo@kubico.test', 'displayName': 'Avó'},
          'households': [
            {'id': 'h1', 'name': 'Casa', 'role': 'Guest', 'access': _access('View', 'View', 'View', 'View')},
          ],
        });
      }
      if (path == '/api/households/h1') {
        return _json({
          'id': 'h1',
          'name': 'Casa',
          'timeZoneId': 'Europe/Lisbon',
          'myRole': 'Guest',
          'members': [
            {'userId': 'avo', 'displayName': 'Avó', 'role': 'Guest', 'color': '#4F7CAC'},
          ],
        });
      }
      if (path == '/api/households/h1/chores') {
        return _json([
          {
            'id': 'c1',
            'householdId': 'h1',
            'title': 'Regar as plantas',
            'assigneeUserId': 'avo',
            'rotationUserIds': <String>[],
            'recurrence': 'None',
            'dueDate': null,
            'points': 1,
            'isDone': false,
            'updatedAt': '2026-10-18T09:00:00Z',
          },
        ]);
      }
      if (path == '/api/households/h1/chores/stats') {
        return _json({'from': '2026-09-18', 'to': '2026-10-18', 'members': []});
      }
      return http.Response('', 404);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
    final session = SessionController(api: api, store: MemoryTokenStore());
    await session.refresh();

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: session),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
          ),
        ],
        child: MaterialApp(home: ChoresView(household: session.currentHousehold!)),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Regar as plantas'), findsOneWidget);
    expect(find.text('Nova tarefa'), findsNothing);
    final button = tester.widget<IconButton>(
      find.ancestor(of: find.byTooltip('Só podes ver as tarefas'), matching: find.byType(IconButton)),
    );
    expect(button.onPressed, isNull);
  });

  testWidgets('quem só vê as compras não adiciona nem marca artigos', (tester) async {
    final client = MockClient((request) async {
      if (request.url.path == '/api/households/h1/lists/l1/items') {
        return _json([
          {
            'id': 'i1',
            'listId': 'l1',
            'name': 'Café',
            'isChecked': false,
            'addedByUserId': 'mae',
            'createdAt': '2026-10-18T09:00:00Z',
            'updatedAt': '2026-10-18T09:00:00Z',
          },
        ]);
      }
      return http.Response('', 404);
    });
    final api = ApiClient(baseUrl: 'http://api', httpClient: client)..accessToken = 't';
    final shopping = ShoppingRepository(
      api: api,
      store: MemoryKeyValueStore(),
      retryEvery: const Duration(hours: 1),
      saveDelay: Duration.zero,
    );
    const list = ShoppingList(id: 'l1', householdId: 'h1', name: 'Supermercado', itemCount: 1, checkedCount: 0);

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider.value(value: api),
          ChangeNotifierProvider.value(value: shopping),
          Provider.value(
            value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
          ),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: ShoppingItemsView(householdId: 'h1', list: list, canEdit: false),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Café'), findsOneWidget);
    expect(find.byType(TextField), findsNothing);
    expect(tester.widget<CheckboxListTile>(find.byType(CheckboxListTile)).onChanged, isNull);
  });
}
