import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';
import 'package:kubico/src/ui/calendar_view.dart';
import 'package:kubico/src/ui/calendar_week_grid.dart';
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
  'occurrenceDate': '2026-11-10',
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
      'occurrenceDate': '2026-11-10',
      'startsAt': _at(11, 10, 10),
      'endsAt': _at(11, 10, 11, 30),
      'startDate': null,
      'endDate': null,
      'conflictUserIds': ['mom'],
    },
    {
      'eventId': 'e2',
      'occurrenceDate': '2026-11-12',
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

  /// Alterações a ocorrências: "PUT /caminho?scope=..." ou "DELETE /caminho?scope=...".
  final occurrenceChanges = <String>[];

  /// Quantos endereços de subscrição já foram gerados.
  var feeds = 1;

  late final client = MockClient((request) async {
    final path = request.url.path;
    if (path == '/api/households/h1') return _json(_household);
    if (path == '/api/households/h1/calendar-feed') {
      if (request.method == 'DELETE') {
        feeds++;
        return http.Response('', 204);
      }
      final url = 'https://api.kubico.test/api/calendar-feeds/segredo-$feeds.ics';
      return _json({'url': url, 'webcalUrl': url.replaceFirst('https', 'webcal')});
    }
    if (path == '/api/households/h1/calendar-sync') {
      return _json({
        'canConnect': false,
        'providers': [
          {'provider': 'Google', 'available': true},
          {'provider': 'Microsoft', 'available': false},
        ],
        'connections': <Object>[],
      });
    }
    if (path.contains('/occurrences/')) {
      occurrenceChanges.add('${request.method} $path?${request.url.query}');
      if (request.method == 'DELETE') return http.Response('', 204);
      final body = jsonDecode(request.body) as Map<String, dynamic>;
      return _json({..._football, 'id': 'e9', 'title': body['title'], 'seriesId': 'e1', 'recurrence': null}, 201);
    }
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

Future<void> _pump(
  WidgetTester tester,
  _FakeApi api,
  HouseholdRole role, {
  double textScale = 1,
  Size size = const Size(1000, 2000),
}) async {
  tester.view.physicalSize = size;
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
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(textScale)),
          child: child!,
        ),
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
      occurrenceDate: DateTime(2026, 11, 12),
      startsAt: null,
      endsAt: null,
      startDate: DateTime(2026, 11, 12),
      endDate: DateTime(2026, 11, 13),
      conflictUserIds: const [],
    );
    final party = CalendarOccurrence(
      eventId: 'e2',
      occurrenceDate: DateTime(2026, 11, 10),
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
    expect(find.text('Mãe tem outro compromisso a essa hora'), findsOneWidget, reason: 'o conflito vê-se sem tooltip');
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

  test('"todos os eventos" muda a série com o mesmo desvio que a ocorrência editada', () {
    final series = CalendarEvent.fromJson(_football);
    final occurrence = CalendarOccurrence(
      eventId: 'e1',
      occurrenceDate: DateTime(2026, 11, 24),
      startsAt: DateTime(2026, 11, 24, 10),
      endsAt: DateTime(2026, 11, 24, 11, 30),
      startDate: null,
      endDate: null,
      conflictUserIds: const [],
    );
    final edited = CalendarEventDraft(
      title: 'Futebol',
      start: DateTime(2026, 11, 24, 11),
      end: DateTime(2026, 11, 24, 13),
      recurrence: 'FREQ=WEEKLY;BYDAY=TU',
    );

    final moved = draftForSeries(edited, series, occurrence);

    expect(moved.start, DateTime(2026, 11, 10, 11), reason: 'uma hora mais tarde, a partir do primeiro dia da série');
    expect(moved.end, DateTime(2026, 11, 10, 13));
    expect(moved.recurrence, 'FREQ=WEEKLY;BYDAY=TU');
  });

  testWidgets('num evento que se repete, alterar pergunta se é só este, os seguintes ou todos', (tester) async {
    final api = _FakeApi();
    await _pump(tester, api, HouseholdRole.adult);

    await tester.tap(find.text('Futebol'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'O que é?'), 'Futebol (jogo fora)');
    await tester.tap(find.text('Guardar'));
    await tester.pumpAndSettle();
    expect(find.text('Alterar evento que se repete'), findsOneWidget);

    await tester.tap(find.text('Só este evento'));
    await tester.pumpAndSettle();
    expect(api.occurrenceChanges, ['PUT /api/households/h1/events/e1/occurrences/2026-11-10?scope=this']);

    await tester.tap(find.text('Futebol'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Apagar'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Este e os seguintes'));
    await tester.pumpAndSettle();
    expect(api.occurrenceChanges.last, 'DELETE /api/households/h1/events/e1/occurrences/2026-11-10?scope=following');
  });

  testWidgets('as crianças não têm endereço para subscrever o calendário fora da app', (tester) async {
    await _pump(tester, _FakeApi(), HouseholdRole.child);

    expect(find.byTooltip('Ver noutros calendários'), findsNothing);
  });

  testWidgets('os outros membros veem o endereço para subscrever noutros calendários e podem trocá-lo', (tester) async {
    final api = _FakeApi();
    await _pump(tester, api, HouseholdRole.guest);

    await tester.tap(find.byTooltip('Ver noutros calendários'));
    await tester.pumpAndSettle();
    expect(find.text('https://api.kubico.test/api/calendar-feeds/segredo-1.ics'), findsOneWidget);

    await tester.tap(find.text('Gerar novo endereço'));
    await tester.pumpAndSettle();
    expect(find.text('https://api.kubico.test/api/calendar-feeds/segredo-2.ics'), findsOneWidget);
    expect(find.text('https://api.kubico.test/api/calendar-feeds/segredo-1.ics'), findsNothing);

    // Daqui também se chega às contas ligadas (nos dois sentidos).
    await tester.tap(find.text('Ligar ao Google Calendar, ao Outlook ou ao iCloud'));
    await tester.pumpAndSettle();
    expect(find.text('Google Calendar, Outlook e iCloud'), findsOneWidget);
    expect(find.text('Só os adultos da casa podem ligar contas.'), findsOneWidget);
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
    expect(find.text('Há um conflito'), findsOneWidget, reason: 'num diálogo, não num aviso que desaparece');
    expect(find.text('Rita tem outro compromisso a essa hora.'), findsOneWidget);
    await tester.tap(find.text('Percebi'));
    await tester.pumpAndSettle();
    expect(find.text('Há um conflito'), findsNothing);
  });

  testWidgets('com a letra a 200 % (avós, baixa visão), a lista e o mês não cortam nada', (tester) async {
    await _pump(tester, _FakeApi(), HouseholdRole.adult, textScale: 2);
    expect(tester.takeException(), isNull);

    await tester.tap(find.text('Mês'));
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
    expect(find.text('10'), findsWidgets);
  });

  group('semana em grelha de horas', () {
    CalendarOccurrence at(String id, int h1, int m1, int h2, int m2) => CalendarOccurrence(
      eventId: id,
      occurrenceDate: DateTime(2026, 11, 10),
      startsAt: DateTime(2026, 11, 10, h1, m1),
      endsAt: DateTime(2026, 11, 10, h2, m2),
      startDate: null,
      endDate: null,
      conflictUserIds: const [],
    );

    test('os eventos que se sobrepõem ficam lado a lado; os seguintes voltam à largura toda', () {
      final blocks = layoutDay([
        at('c', 11, 0, 12, 0),
        at('a', 9, 0, 10, 0),
        at('b', 9, 30, 11, 0),
      ], DateTime(2026, 11, 10));

      final byId = {for (final b in blocks) b.occurrence.eventId: (b.column, b.columns)};
      expect(byId, {'a': (0, 2), 'b': (1, 2), 'c': (0, 1)});
    });

    test('as horas vão das 7 às 22, alargadas para o que houver fora disso', () {
      final day = DateTime(2026, 11, 10);
      CalendarRange range(List<CalendarOccurrence> o) =>
          CalendarRange(events: {'a': CalendarEvent.fromJson(_football)}, occurrences: o, holidays: const []);

      expect(WeekTimeGrid.hoursFor([day], range([at('a', 9, 0, 10, 0)])), (7, 22));
      expect(WeekTimeGrid.hoursFor([day], range([at('a', 6, 30, 23, 15)])), (6, 24));
    });
  });

  testWidgets('a semana em grelha num telemóvel com letra a 200 %: sem cortes, e tocar num dia abre-o', (tester) async {
    final api = _FakeApi();
    await _pump(tester, api, HouseholdRole.child, textScale: 2, size: const Size(360, 780));

    await tester.tap(find.text('Semana'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(find.text('Futebol'), findsOneWidget);

    // Uma criança só vê: tocar no evento abre o dia.
    await tester.ensureVisible(find.text('Futebol'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Futebol'));
    await tester.pumpAndSettle();
    expect(find.text('Terça-feira, 10 de novembro'), findsWidgets, reason: 'a vista de dia (título e cabeçalho)');

    await tester.tap(find.text('Semana'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('week-day-20261111')));
    await tester.pumpAndSettle();
    expect(find.text('Quarta-feira, 11 de novembro'), findsWidgets);
  });
}
