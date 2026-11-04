import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/notifications/open_notification.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/session/session_controller.dart';
import 'package:kubico/src/session/token_store.dart';
import 'package:kubico/src/ui/pdf_preview_screen.dart';
import 'package:kubico/src/ui/home_area_screen.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

class _Server {
  final requests = <String>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (request.method != 'GET') requests.add('${request.method} $path ${request.body}');
    return switch ((request.method, path)) {
      ('GET', '/api/me') => _json({
        'user': {'id': 'mae', 'email': 'mae@kubico.test', 'displayName': 'Mãe', 'isChildProfile': false},
        'households': [
          {'id': 'h1', 'name': 'Casa', 'role': 'Admin'},
        ],
      }),
      ('GET', '/api/households/h1') => _json({
        'id': 'h1',
        'name': 'Casa',
        'timeZoneId': 'Europe/Lisbon',
        'myRole': 'Admin',
        'members': [
          {'userId': 'mae', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#2F64A8', 'isChildProfile': false},
        ],
      }),
      ('GET', '/api/households/h1/home') => _json({
        'appliances': [
          {
            'id': 'a1',
            'name': 'Carro',
            'kind': 'Car',
            'room': null,
            'brand': null,
            'model': null,
            'purchasedOn': null,
            'warrantyUntil': null,
            'plate': 'AA-00-BB',
            'firstRegistration': '2020-05-10',
            'nextIuc': '2027-05-01',
            'nextInspection': '2028-05-10',
            'notes': null,
            'maintenance': [
              {'id': 'm1', 'title': 'Revisão', 'intervalDays': 365, 'lastDone': null, 'nextDue': '2027-01-10'},
            ],
            'files': [
              {'id': 'f1', 'fileName': 'fatura.pdf', 'contentType': 'application/pdf', 'size': 9},
            ],
          },
        ],
        'documents': [
          {
            'id': 'd1',
            'kind': 'Passport',
            'name': 'Passaporte da Mãe',
            'ownerUserId': 'mae',
            'number': 'P999',
            'expiresOn': '2028-01-01',
            'files': <Object>[],
          },
        ],
        'notes': [
          {
            'id': 'n1',
            'text': 'Hoje há jantar fora',
            'until': null,
            'createdByUserId': 'mae',
            'createdAt': '2026-11-10T09:00:00Z',
          },
        ],
        'contacts': [
          {'id': 'c1', 'name': 'Dr. Silva', 'role': 'Médico', 'phone': '210000000', 'notes': null, 'birthday': null},
        ],
        'birthdays': [
          {'name': 'Avó', 'date': '2026-11-12'},
        ],
        'irsReminders': true,
      }),
      ('GET', '/api/households/h1/home/files/f1') => http.Response.bytes(
        utf8.encode('%PDF-1.4 '),
        200,
        headers: {'content-type': 'application/pdf'},
      ),
      ('POST', _) => _json({}, 201),
      _ => http.Response('', 404),
    };
  });
}

void main() {
  test('os avisos da Casa abrem a área Casa', () {
    expect(NotificationTarget.of('HomeReminder', 'h1')?.module, HouseholdModule.home);
  });

  testWidgets('a casa mostra recados, o carro com o IUC e a inspeção, o cofre e os aniversários', (tester) async {
    tester.view.physicalSize = const Size(600, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final server = _Server();
    final api = ApiClient(baseUrl: 'http://api', httpClient: server.client)..accessToken = 't';
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
        child: const MaterialApp(
          home: HomeAreaScreen(
            household: HouseholdSummary(id: 'h1', name: 'Casa', role: HouseholdRole.admin),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Hoje há jantar fora'), findsOneWidget);

    await tester.tap(find.text('Equipamentos'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Carro'));
    await tester.pumpAndSettle();
    expect(find.text('IUC: pagar em 1/5/2027'), findsOneWidget);
    expect(find.text('Próxima inspeção: 10/5/2028'), findsOneWidget);
    await tester.tap(find.text('Feita'));
    await tester.pumpAndSettle();
    expect(server.requests.last, 'POST /api/households/h1/home/appliances/a1/maintenance/m1/done ');

    // O PDF abre-se dentro da app (o PDFium não corre nos testes: um visualizador falso mostra o que recebeu).
    PdfPreviewScreen.debugViewer = (bytes, name) => Text('PDF $name com ${bytes.length} bytes');
    addTearDown(() => PdfPreviewScreen.debugViewer = null);
    await tester.tap(find.text('fatura.pdf'));
    await tester.pumpAndSettle();
    expect(find.text('PDF fatura.pdf com 9 bytes'), findsOneWidget);
    expect(find.byTooltip('Guardar no dispositivo'), findsOneWidget);
    await tester.pageBack();
    await tester.pumpAndSettle();

    await tester.tap(find.text('Documentos'));
    await tester.pumpAndSettle();
    expect(find.textContaining('P999'), findsOneWidget);

    await tester.tap(find.text('Contactos'));
    await tester.pumpAndSettle();
    expect(find.text('Avó'), findsOneWidget);
    expect(find.text('Dr. Silva'), findsOneWidget);
  });
}
