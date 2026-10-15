import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/calendar_view.dart';
import 'package:provider/provider.dart';

http.Response _json(Object body, [int status = 200]) => http.Response.bytes(
  utf8.encode(jsonEncode(body)),
  status,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

/// Instante UTC de uma hora local, para os testes não dependerem do fuso da máquina.
String _at(int month, int day, int hour, [int minute = 0]) =>
    DateTime(2026, month, day, hour, minute).toUtc().toIso8601String();

const _household = {
  'id': 'h1',
  'name': 'Casa',
  'timeZoneId': 'Europe/Lisbon',
  'myRole': 'Adult',
  'members': [
    {'userId': 'mom', 'displayName': 'Mãe', 'role': 'Admin', 'color': '#4F7CAC'},
    {'userId': 'dad', 'displayName': 'Pai', 'role': 'Adult', 'color': '#81B29A'},
    {'userId': 'kid', 'displayName': 'Rita', 'role': 'Child', 'color': '#E07A5F'},
  ],
};

final _football = {
  'id': 'e1',
  'householdId': 'h1',
  'title': 'Futebol',
  'notes': null,
  'location': null,
  'isAllDay': false,
  'startsAt': _at(11, 10, 10),
  'endsAt': _at(11, 10, 11, 30),
  'startDate': null,
  'endDate': null,
  'recurrence': 'FREQ=WEEKLY;BYDAY=TU',
  'participantUserIds': ['kid'],
  'dropOffUserId': 'mom',
  'pickUpUserId': 'dad',
  'updatedAt': '2026-10-15T10:00:00Z',
};

final _holidays = {
  'id': 'e2',
  'householdId': 'h1',
  'title': 'Férias',
  'notes': null,
  'location': 'Algarve',
  'isAllDay': true,
  'startsAt': null,
  'endsAt': null,
  'startDate': '2026-11-12',
  'endDate': '2026-11-13',
  'recurrence': null,
  'participantUserIds': <String>[],
  'dropOffUserId': null,
  'pickUpUserId': null,
  'updatedAt': '2026-10-15T10:00:00Z',
};

final _dentist = {
  'id': 'e3',
  'householdId': 'h1',
  'title': 'Dentista',
  'notes': null,
  'location': null,
  'isAllDay': false,
  'startsAt': _at(11, 10, 9),
  'endsAt': _at(11, 10, 10),
  'startDate': null,
  'endDate': null,
  'recurrence': null,
  'participantUserIds': ['kid'],
  'dropOffUserId': null,
  'pickUpUserId': null,
  'updatedAt': '2026-10-15T11:00:00Z',
};

final _dentistOccurrence = {
  'eventId': 'e3',
  'startsAt': _at(11, 10, 9),
  'endsAt': _at(11, 10, 10),
  'startDate': null,
  'endDate': null,
  'conflictUserIds': ['kid'],
};

Map<String, dynamic> _calendar({required bool withDentist}) => {
  'events': [_football, _holidays, if (withDentist) _dentist],
  'occurrences': [
    {
      'eventId': 'e1',
      'startsAt': _at(11, 10, 10),
      'endsAt': _at(11, 10, 11, 30),
      'startDate': null,
      'endDate': null,
      'conflictUserIds': ['mom'],
    },
    {
      'eventId': 'e2',
      'startsAt': null,
      'endsAt': null,
      'startDate': '2026-11-12',
      'endDate': '2026-11-13',
      'conflictUserIds': <String>[],
    },
    if (withDentist) _dentistOccurrence,
  ],
  'holidays': [
    {'date': '2026-11-01', 'name': 'Todos os Santos'},
    {'date': '2026-12-01', 'name': 'Restauração da Independência'},
  ],
};

/// API falsa com três membros, um treino de futebol com boleias e um conflito, e uns dias de férias.
class _FakeApi {
  final ranges = <String>[];
  final posts = <Map<String, dynamic>>[];

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (path == '/api/households/h1') return _json(_household);
    if (path == '/api/households/h1/events' && request.method == 'POST') {
      posts.add(jsonDecode(request.body) as Map<String, dynamic>);
      return _json(_dentist, 201);
    }
    if (path == '/api/households/h1/events') {
      ranges.add(request.url.query);
      return _json(_calendar(withDentist: posts.isNotEmpty));
    }
    return http.Response('', 404);
  });
}

Future<void> _pump(WidgetTester tester, _FakeApi api, HouseholdRole role) async {
  tester.view.physicalSize = const Size(1000, 2000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final client = ApiClient(baseUrl: 'http://api', httpClient: api.client)..accessToken = 't';
  await tester.pumpWidget(
    MultiProvider(
      providers: [
        Provider.value(value: client),
        Provider.value(
          value: HouseholdRealtime(baseUrl: 'http://api', accessToken: () => 't'),
        ),
      ],
      child: MaterialApp(
        home: CalendarView(
          household: HouseholdSummary(id: 'h1', name: 'Casa', role: role),
          initialDate: DateTime(2026, 11, 10),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  group('vistas', () {
    test('o mês mostra seis semanas, a começar à segunda-feira', () {
      final (first, last) = visibleRange(CalendarMode.month, DateTime(2026, 10, 15));
      expect(first, DateTime(2026, 9, 28));
      expect(last, DateTime(2026, 11, 8));

      final (february, _) = visibleRange(CalendarMode.month, DateTime(2027, 2, 20));
      expect(february, DateTime(2027, 2, 1));
    });

    test('a semana vai de segunda a domingo', () {
      final (monday, sunday) = visibleRange(CalendarMode.week, DateTime(2026, 10, 18));
      expect(monday, DateTime(2026, 10, 12));
      expect(sunday, DateTime(2026, 10, 18));
    });

    test('avançar um mês a partir de dia 31 não salta meses', () {
      expect(shiftFocus(CalendarMode.month, DateTime(2026, 1, 31), 1), DateTime(2026, 2));
      expect(shiftFocus(CalendarMode.week, DateTime(2026, 10, 30), -1), DateTime(2026, 10, 23));
    });

    test('títulos em português', () {
      expect(rangeTitle(CalendarMode.month, DateTime(2026, 11, 10)), 'Novembro de 2026');
      expect(rangeTitle(CalendarMode.week, DateTime(2026, 11, 12)), '9 – 15 de novembro');
      expect(rangeTitle(CalendarMode.week, DateTime(2026, 10, 30)), '26 de outubro – 1 de novembro');
      expect(rangeTitle(CalendarMode.day, DateTime(2026, 11, 10)), 'Terça-feira, 10 de novembro');
    });
  });

  group('Recurrence', () {
    test('lê e escreve a RRULE da API', () {
      final rule = Recurrence.parse('FREQ=WEEKLY;BYDAY=TU,TH;UNTIL=20261231')!;

      expect(rule.frequency, RecurrenceFrequency.weekly);
      expect(rule.weekdays, [DateTime.tuesday, DateTime.thursday]);
      expect(rule.until, DateTime(2026, 12, 31));
      expect(rule.toRule(), 'FREQ=WEEKLY;BYDAY=TU,TH;UNTIL=20261231');
      expect(describeRecurrence(rule), 'Todas as semanas (ter, qui) até 31/12/2026');
    });

    test('mantém o intervalo e o número de vezes', () {
      final rule = Recurrence.parse('FREQ=MONTHLY;INTERVAL=2;COUNT=5')!;

      expect(rule.toRule(), 'FREQ=MONTHLY;INTERVAL=2;COUNT=5');
      expect(describeRecurrence(rule), 'De 2 em 2 meses, 5 vezes');
    });

    test('sem regra, ou com uma frequência desconhecida, não há repetição', () {
      expect(Recurrence.parse(null), isNull);
      expect(Recurrence.parse('FREQ=HOURLY'), isNull);
    });
  });

  test('eventos de dia inteiro enviam dias; os outros, instantes UTC', () {
    final allDay = CalendarEventDraft(
      title: 'Férias',
      start: DateTime(2026, 8, 1),
      end: DateTime(2026, 8, 15),
      isAllDay: true,
    );
    final timed = CalendarEventDraft(
      title: 'Natação',
      location: 'Piscina municipal',
      start: DateTime(2026, 11, 10, 18),
      end: DateTime(2026, 11, 10, 19),
    );

    final allDayJson = allDay.toJson();
    final timedJson = timed.toJson();
    expect((allDayJson['startDate'], allDayJson['endDate']), ('2026-08-01', '2026-08-15'));
    expect(allDayJson.containsKey('startsAt'), isFalse);
    expect(timedJson['startsAt'], DateTime(2026, 11, 10, 18).toUtc().toIso8601String());
    expect(timedJson.containsKey('startDate'), isFalse);
  });

  test('uma ocorrência aparece em todos os dias que ocupa', () {
    final trip = CalendarOccurrence(
      eventId: 'e1',
      startsAt: null,
      endsAt: null,
      startDate: DateTime(2026, 11, 12),
      endDate: DateTime(2026, 11, 13),
      conflictUserIds: const [],
    );
    final party = CalendarOccurrence(
      eventId: 'e2',
      startsAt: DateTime(2026, 11, 10, 23),
      endsAt: DateTime(2026, 11, 11, 1),
      startDate: null,
      endDate: null,
      conflictUserIds: const [],
    );

    expect([11, 12, 13, 14].map((d) => trip.occursOn(DateTime(2026, 11, d))), [false, true, true, false]);
    expect([10, 11, 12].map((d) => party.occursOn(DateTime(2026, 11, d))), [true, true, false]);
  });

  testWidgets('mostra boleias, conflitos e feriados; crianças só veem', (tester) async {
    final api = _FakeApi();
    await _pump(tester, api, HouseholdRole.child);

    expect(api.ranges, ['from=2026-10-26&to=2026-12-06']);
    expect(find.text('Novembro de 2026'), findsOneWidget);
    expect(find.text('Terça-feira, 10 de novembro'), findsOneWidget);
    expect(find.text('Futebol'), findsOneWidget);
    expect(find.textContaining('Leva: Mãe · Vai buscar: Pai'), findsOneWidget);
    expect(find.byTooltip('Mãe tem outro compromisso a essa hora'), findsOneWidget);
    expect(find.text('Novo evento'), findsNothing, reason: 'crianças não criam eventos');

    await tester.tap(find.byKey(ValueKey(DateTime(2026, 11, 1))));
    await tester.pumpAndSettle();
    expect(find.text('Domingo, 1 de novembro · Todos os Santos'), findsOneWidget);

    await tester.tap(find.byKey(ValueKey(DateTime(2026, 11, 12))));
    await tester.pumpAndSettle();
    expect(find.text('Férias'), findsOneWidget);
    expect(find.text('Futebol'), findsNothing);

    await tester.tap(find.text('Semana'));
    await tester.pumpAndSettle();
    expect(api.ranges.last, 'from=2026-11-09&to=2026-11-15');
    expect(find.text('9 – 15 de novembro'), findsOneWidget);
    expect(find.text('Futebol'), findsOneWidget);
    expect(find.text('Férias'), findsNWidgets(2));
  });

  testWidgets('um adulto cria um evento e é avisado do conflito', (tester) async {
    final api = _FakeApi();
    await _pump(tester, api, HouseholdRole.adult);

    await tester.tap(find.text('Novo evento'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'O que é?'), 'Dentista');
    await tester.tap(find.widgetWithText(FilterChip, 'Rita'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();

    final body = api.posts.single;
    final start = DateTime.parse(body['startsAt'] as String).toLocal();
    final end = DateTime.parse(body['endsAt'] as String).toLocal();
    expect(body['title'], 'Dentista');
    expect(body['participantUserIds'], ['kid']);
    expect(DateUtils.dateOnly(start), DateTime(2026, 11, 10));
    expect(end.difference(start), const Duration(hours: 1));
    expect(find.text('Atenção: Rita tem outro compromisso a essa hora.'), findsOneWidget);
  });
}
