import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import 'calendar_event_editor.dart';
import 'dates_pt.dart';
import 'theme.dart';

enum CalendarMode {
  day('Dia'),
  week('Semana'),
  month('Mês');

  const CalendarMode(this.label);
  final String label;
}

/// Primeiro e último dia (inclusive) de cada vista. As semanas começam à segunda-feira
/// e o mês mostra sempre seis semanas completas.
(DateTime, DateTime) visibleRange(CalendarMode mode, DateTime focused) {
  final day = DateUtils.dateOnly(focused);
  switch (mode) {
    case CalendarMode.day:
      return (day, day);
    case CalendarMode.week:
      final monday = DateTime(day.year, day.month, day.day - day.weekday + 1);
      return (monday, DateTime(monday.year, monday.month, monday.day + 6));
    case CalendarMode.month:
      final start = DateTime(day.year, day.month, 2 - DateTime(day.year, day.month).weekday);
      return (start, DateTime(start.year, start.month, start.day + 41));
  }
}

/// O dia em foco depois de avançar ([delta] = 1) ou recuar (-1) uma vista.
DateTime shiftFocus(CalendarMode mode, DateTime focused, int delta) => switch (mode) {
  CalendarMode.day => DateTime(focused.year, focused.month, focused.day + delta),
  CalendarMode.week => DateTime(focused.year, focused.month, focused.day + 7 * delta),
  CalendarMode.month => DateTime(focused.year, focused.month + delta),
};

/// Título da vista: "Outubro de 2026", "12 – 18 de outubro" ou "Terça-feira, 20 de outubro".
String rangeTitle(CalendarMode mode, DateTime focused) {
  final (first, last) = visibleRange(mode, focused);
  return switch (mode) {
    CalendarMode.day => formatLongDate(focused),
    CalendarMode.week when first.month == last.month => '${first.day} – ${formatDayMonth(last)}',
    CalendarMode.week => '${formatDayMonth(first)} – ${formatDayMonth(last)}',
    CalendarMode.month => capitalize('${monthName(focused.month)} de ${focused.year}'),
  };
}

/// Os dias de [first] a [last], inclusive.
List<DateTime> daysIn(DateTime first, DateTime last) {
  return [for (var d = first; !d.isAfter(last); d = DateTime(d.year, d.month, d.day + 1)) d];
}

/// Texto curto da repetição, ex.: "Todas as semanas (ter, qui) até 31/12/2026".
String describeRecurrence(Recurrence r) {
  final base = r.interval == 1 ? r.frequency.label : 'De ${r.interval} em ${r.interval} ${r.frequency.unit}';
  final text = StringBuffer(base);
  if (r.frequency == RecurrenceFrequency.weekly && r.weekdays.isNotEmpty) {
    final days = [...r.weekdays]..sort();
    text.write(' (${days.map((d) => weekdayShortNames[d - 1]).join(', ')})');
  }
  if (r.until != null) text.write(' até ${formatShortDate(r.until!)}');
  if (r.count != null) text.write(', ${r.count} vezes');
  return text.toString();
}

String _initial(String name) => name.isEmpty ? '?' : name[0].toUpperCase();

/// Calendário partilhado da casa: vistas de dia, semana e mês, com a cor de cada membro,
/// feriados nacionais e avisos de conflito. Sincroniza em tempo real.
class CalendarView extends StatefulWidget {
  const CalendarView({super.key, required this.household, this.initialDate});

  final HouseholdSummary household;

  /// Dia aberto ao início; por omissão, hoje.
  final DateTime? initialDate;

  @override
  State<CalendarView> createState() => _CalendarViewState();
}

class _CalendarViewState extends State<CalendarView> {
  final _subscriptions = <StreamSubscription<Object?>>[];
  var _mode = CalendarMode.month;
  late DateTime _selected = DateUtils.dateOnly(widget.initialDate ?? DateTime.now());
  CalendarRange? _calendar;
  (DateTime, DateTime)? _loadedRange;
  HouseholdDetails? _details;
  Object? _error;

  /// Só conta a resposta do pedido mais recente (ao navegar depressa, as anteriores chegam tarde).
  var _request = 0;

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _subscriptions
      ..add(realtime.events.listen(_onEvent))
      ..add(realtime.reconnected.listen((_) => _load()));
    _load();
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    super.dispose();
  }

  Future<void> _load() async {
    final range = visibleRange(_mode, _selected);
    final request = ++_request;
    try {
      final results = await Future.wait([
        _api.getCalendar(_householdId, range.$1, range.$2),
        _api.getHousehold(_householdId),
      ]);
      if (!mounted || request != _request) return;
      setState(() {
        _calendar = results[0] as CalendarRange;
        _details = results[1] as HouseholdDetails;
        _loadedRange = range;
        _error = null;
      });
    } catch (e) {
      if (mounted && request == _request) setState(() => _error = e);
    }
  }

  void _onEvent(HouseholdEvent event) {
    switch (event) {
      case CalendarEventUpserted(event: final changed) when changed.householdId == _householdId:
        // Ignora versões que já temos (por exemplo, o eco das nossas próprias alterações).
        final known = _calendar?.events[changed.id];
        if (known == null || changed.updatedAt.isAfter(known.updatedAt)) _load();
      case CalendarEventDeleted(:final eventId) when _calendar?.events.containsKey(eventId) ?? false:
        _load();
      default:
        break;
    }
  }

  void _go(DateTime day, [CalendarMode? mode]) {
    setState(() {
      _selected = DateUtils.dateOnly(day);
      _mode = mode ?? _mode;
    });
    if (visibleRange(_mode, _selected) != _loadedRange) _load();
  }

  /// A cor de um evento é a do primeiro participante; sem participantes, a da app.
  Color _colorFor(CalendarEvent event) {
    for (final id in event.participantUserIds) {
      final member = _details?.member(id);
      if (member != null) return colorFromHex(member.color);
    }
    return Theme.of(context).colorScheme.primary;
  }

  /// Ex.: "Ana e Rui têm outro compromisso a essa hora".
  String _unavailable(Iterable<String> userIds) {
    final names = [for (final id in userIds) _details?.member(id)?.displayName ?? 'Alguém'];
    final who = names.length == 1 ? names.single : '${names.take(names.length - 1).join(', ')} e ${names.last}';
    return '$who ${names.length == 1 ? 'tem' : 'têm'} outro compromisso a essa hora';
  }

  Future<void> _edit([CalendarEvent? event]) async {
    final details = _details;
    if (details == null) return;
    final result = await showCalendarEventEditor(context, members: details.members, day: _selected, event: event);
    if (result == null || !mounted) return;
    try {
      switch (result) {
        case CalendarEventSaved(:final draft) when event == null:
          await _afterSave(await _api.createEvent(_householdId, draft));
        case CalendarEventSaved(:final draft):
          await _afterSave(await _api.updateEvent(_householdId, event!.id, draft));
        case CalendarEventDeleteRequested():
          await _api.deleteEvent(_householdId, event!.id);
          await _load();
      }
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  /// Depois de guardar, abre o dia do evento e avisa se alguém não está disponível.
  Future<void> _afterSave(CalendarEvent saved) async {
    setState(() => _selected = DateUtils.dateOnly(saved.isAllDay ? saved.startDate! : saved.startsAt!));
    await _load();
    if (!mounted) return;
    final busy = _calendar?.conflictsFor(saved.id) ?? const <String>{};
    if (busy.isEmpty) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text('Atenção: ${_unavailable(busy)}.')));
  }

  @override
  Widget build(BuildContext context) {
    final calendar = _calendar;
    final canManage = widget.household.role.canManageContent;

    return Scaffold(
      floatingActionButton: canManage
          ? FloatingActionButton.extended(
              onPressed: _edit,
              icon: const Icon(Icons.add),
              label: const Text('Novo evento'),
            )
          : null,
      body: Column(
        children: [
          _toolbar(context),
          Expanded(child: calendar == null ? _placeholder() : _content(calendar)),
        ],
      ),
    );
  }

  Widget _toolbar(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 8, 8, 0),
      child: Column(
        children: [
          SegmentedButton<CalendarMode>(
            segments: [for (final m in CalendarMode.values) ButtonSegment(value: m, label: Text(m.label))],
            selected: {_mode},
            showSelectedIcon: false,
            onSelectionChanged: (selection) => _go(_selected, selection.single),
          ),
          Row(
            children: [
              IconButton(
                tooltip: 'Anterior',
                onPressed: () => _go(shiftFocus(_mode, _selected, -1)),
                icon: const Icon(Icons.chevron_left),
              ),
              Expanded(
                child: Text(
                  rangeTitle(_mode, _selected),
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              IconButton(
                tooltip: 'Seguinte',
                onPressed: () => _go(shiftFocus(_mode, _selected, 1)),
                icon: const Icon(Icons.chevron_right),
              ),
              TextButton(onPressed: () => _go(DateTime.now()), child: const Text('Hoje')),
            ],
          ),
        ],
      ),
    );
  }

  Widget _placeholder() {
    return Center(
      child: _error == null
          ? const CircularProgressIndicator()
          : Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(_error.toString()),
                TextButton(onPressed: _load, child: const Text('Tentar de novo')),
              ],
            ),
    );
  }

  Widget _content(CalendarRange calendar) {
    final (first, last) = visibleRange(_mode, _selected);
    final days = _mode == CalendarMode.month ? [_selected] : daysIn(first, last);
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
        children: [
          if (_mode == CalendarMode.month)
            _MonthGrid(
              start: first,
              month: _selected.month,
              selected: _selected,
              calendar: calendar,
              colorFor: _colorFor,
              onSelect: _go,
            ),
          for (final day in days) ..._daySection(calendar, day),
        ],
      ),
    );
  }

  List<Widget> _daySection(CalendarRange calendar, DateTime day) {
    final theme = Theme.of(context);
    final holiday = calendar.holidayOn(day);
    final occurrences = calendar.on(day);
    return [
      Padding(
        padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
        child: Text(
          holiday == null ? formatLongDate(day) : '${formatLongDate(day)} · ${holiday.name}',
          style: theme.textTheme.titleSmall?.copyWith(color: holiday == null ? null : theme.colorScheme.error),
        ),
      ),
      if (occurrences.isEmpty)
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          child: Text('Sem eventos.', style: TextStyle(color: theme.colorScheme.outline)),
        ),
      for (final o in occurrences) _tile(o, calendar.events[o.eventId]!, day),
    ];
  }

  Widget _tile(CalendarOccurrence occurrence, CalendarEvent event, DateTime day) {
    return _OccurrenceTile(
      occurrence: occurrence,
      event: event,
      day: day,
      color: _colorFor(event),
      details: _details,
      conflicts: occurrence.conflictUserIds.isEmpty ? null : _unavailable(occurrence.conflictUserIds),
      onTap: widget.household.role.canManageContent ? () => _edit(event) : null,
    );
  }
}

class _MonthGrid extends StatelessWidget {
  const _MonthGrid({
    required this.start,
    required this.month,
    required this.selected,
    required this.calendar,
    required this.colorFor,
    required this.onSelect,
  });

  /// Segunda-feira da primeira semana mostrada.
  final DateTime start;
  final int month;
  final DateTime selected;
  final CalendarRange calendar;
  final Color Function(CalendarEvent event) colorFor;
  final ValueChanged<DateTime> onSelect;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final today = DateUtils.dateOnly(DateTime.now());
    return Column(
      children: [
        Row(
          children: [
            for (final name in weekdayShortNames)
              Expanded(
                child: Center(child: Text(name, style: theme.textTheme.labelSmall)),
              ),
          ],
        ),
        for (var week = 0; week < 6; week++) _week(week, today),
      ],
    );
  }

  Widget _week(int week, DateTime today) {
    final days = [for (var i = 0; i < 7; i++) DateTime(start.year, start.month, start.day + 7 * week + i)];
    return Row(children: [for (final day in days) Expanded(child: _cell(day, today))]);
  }

  Widget _cell(DateTime day, DateTime today) {
    final occurrences = calendar.on(day);
    return _DayCell(
      key: ValueKey(day),
      day: day,
      inMonth: day.month == month,
      isToday: day == today,
      isSelected: day == selected,
      isHoliday: calendar.holidayOn(day) != null,
      dots: [for (final o in occurrences.take(3)) colorFor(calendar.events[o.eventId]!)],
      onTap: () => onSelect(day),
    );
  }
}

class _DayCell extends StatelessWidget {
  const _DayCell({
    super.key,
    required this.day,
    required this.inMonth,
    required this.isToday,
    required this.isSelected,
    required this.isHoliday,
    required this.dots,
    required this.onTap,
  });

  final DateTime day;
  final bool inMonth;
  final bool isToday;
  final bool isSelected;
  final bool isHoliday;

  /// Cores dos primeiros eventos do dia.
  final List<Color> dots;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final dayColor = isHoliday ? scheme.error : (inMonth ? scheme.onSurface : scheme.outline);
    return Padding(
      padding: const EdgeInsets.all(2),
      child: Material(
        color: isSelected ? scheme.primaryContainer : Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(12),
          side: isToday ? BorderSide(color: scheme.primary) : BorderSide.none,
        ),
        child: InkWell(
          onTap: onTap,
          customBorder: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          child: SizedBox(
            height: 48,
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Text(
                  '${day.day}',
                  style: TextStyle(color: dayColor, fontWeight: isToday ? FontWeight.bold : null),
                ),
                const SizedBox(height: 4),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [for (final color in dots) _Dot(color: color)],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Dot extends StatelessWidget {
  const _Dot({required this.color});

  final Color color;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 6,
      height: 6,
      margin: const EdgeInsets.symmetric(horizontal: 1),
      decoration: BoxDecoration(color: color, shape: BoxShape.circle),
    );
  }
}

class _OccurrenceTile extends StatelessWidget {
  const _OccurrenceTile({
    required this.occurrence,
    required this.event,
    required this.day,
    required this.color,
    required this.details,
    required this.conflicts,
    this.onTap,
  });

  final CalendarOccurrence occurrence;
  final CalendarEvent event;
  final DateTime day;
  final Color color;
  final HouseholdDetails? details;

  /// Aviso de conflito, se houver (ex.: "Ana tem outro compromisso a essa hora").
  final String? conflicts;
  final VoidCallback? onTap;

  String _name(String? userId) => details?.member(userId)?.displayName ?? '?';

  /// Hora de início e de fim neste dia; "…" quando o evento começa ou acaba noutro dia.
  (String, String?) _times() {
    if (occurrence.isAllDay) return ('Todo o dia', null);
    final start = occurrence.startsAt!;
    final end = occurrence.endsAt!;
    final nextDay = DateTime(day.year, day.month, day.day + 1);
    return (
      DateUtils.isSameDay(start, day) ? formatTime(start) : '…',
      DateUtils.isSameDay(end, day) || end == nextDay ? formatTime(end) : '…',
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final (from, to) = _times();
    final recurrence = Recurrence.parse(event.recurrence);
    final subtitle = [
      if (event.location != null) event.location!,
      if (recurrence != null) describeRecurrence(recurrence),
      if (event.dropOffUserId != null) 'Leva: ${_name(event.dropOffUserId)}',
      if (event.pickUpUserId != null) 'Vai buscar: ${_name(event.pickUpUserId)}',
    ].join(' · ');
    final participants = [for (final id in event.participantUserIds) details?.member(id)].whereType<Member>();

    return Card(
      child: ListTile(
        onTap: onTap,
        leading: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 4,
              height: 36,
              decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(2)),
            ),
            const SizedBox(width: 12),
            // As horas encolhem em vez de partir em várias linhas (ecrãs estreitos, letra grande).
            SizedBox(
              width: 52,
              child: FittedBox(
                fit: BoxFit.scaleDown,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(from, style: theme.textTheme.labelLarge),
                    if (to != null) Text(to, style: theme.textTheme.bodySmall),
                  ],
                ),
              ),
            ),
          ],
        ),
        title: Text(event.title),
        subtitle: subtitle.isEmpty ? null : Text(subtitle),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (conflicts != null)
              Tooltip(
                message: conflicts!,
                child: Icon(Icons.warning_amber_rounded, color: theme.colorScheme.error),
              ),
            for (final member in participants.take(3))
              Padding(
                padding: const EdgeInsets.only(left: 4),
                child: CircleAvatar(
                  radius: 12,
                  backgroundColor: colorFromHex(member.color),
                  foregroundColor: Colors.white,
                  child: Text(_initial(member.displayName), style: const TextStyle(fontSize: 11)),
                ),
              ),
          ],
        ),
      ),
    );
  }
}
