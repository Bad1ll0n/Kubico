import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import 'calendar_event_editor.dart';
import 'calendar_week_grid.dart';
import 'calendar_feed_dialog.dart' deferred as feed;
import 'dates_pt.dart';
import 'design/components.dart';
import 'theme.dart';

enum CalendarMode {
  day('Dia'),
  week('Semana'),
  month('Mês');

  const CalendarMode(this._label);
  final String _label;

  String get label => _label.tr;
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
    CalendarMode.month => capitalize('${monthName(focused.month)} de ${focused.year}'.tr),
  };
}

/// Os dias de [first] a [last], inclusive.
List<DateTime> daysIn(DateTime first, DateTime last) {
  return [for (var d = first; !d.isAfter(last); d = DateTime(d.year, d.month, d.day + 1)) d];
}

/// Texto curto da repetição, ex.: "Todas as semanas (ter, qui) até 31/12/2026".
String describeRecurrence(Recurrence r) {
  final base = r.interval == 1 ? r.frequency.label : 'De ${r.interval} em ${r.interval} ${r.frequency.unit}'.tr;
  final text = StringBuffer(base);
  if (r.frequency == RecurrenceFrequency.weekly && r.weekdays.isNotEmpty) {
    final days = [...r.weekdays]..sort();
    text.write(' (${days.map((d) => weekdayShortNames[d - 1]).join(', ')})');
  }
  if (r.until != null) text.write(' até ${formatShortDate(r.until!)}'.tr);
  if (r.count != null) text.write(', ${r.count} vezes'.tr);
  return text.toString();
}

/// A que ocorrências de um evento que se repete se aplica uma alteração.
enum RecurringScope {
  onlyThis('Só este evento'),
  following('Este e os seguintes'),
  all('Todos os eventos');

  const RecurringScope(this._label);
  final String _label;

  String get label => _label.tr;
}

int _daysBetween(DateTime a, DateTime b) =>
    DateTime.utc(b.year, b.month, b.day).difference(DateTime.utc(a.year, a.month, a.day)).inDays;

/// Para "todos os eventos": o editor mostrou a [occurrence] escolhida, por isso a série muda com o mesmo desvio
/// (passar esta ocorrência das 18:00 para as 19:00 passa todas para as 19:00).
CalendarEventDraft draftForSeries(CalendarEventDraft edited, CalendarEvent series, CalendarOccurrence occurrence) {
  if (edited.isAllDay && series.isAllDay) {
    final shift = _daysBetween(occurrence.startDate!, edited.start);
    final span = _daysBetween(edited.start, edited.end);
    final first = series.startDate!;
    final start = DateTime(first.year, first.month, first.day + shift);
    return edited.copyWith(start: start, end: DateTime(start.year, start.month, start.day + span));
  }
  if (!edited.isAllDay && !series.isAllDay) {
    final start = series.startsAt!.add(edited.start.difference(occurrence.startsAt!));
    return edited.copyWith(start: start, end: start.add(edited.end.difference(edited.start)));
  }

  // Passou de dia inteiro para hora marcada (ou o contrário): fica no primeiro dia da série, com as horas novas.
  final first = series.startDate ?? series.startsAt!;
  final start = DateTime(first.year, first.month, first.day, edited.start.hour, edited.start.minute);
  if (edited.isAllDay) {
    // Em dias, não em horas: no dia da mudança de hora de outubro, "+ 24 h" ainda é o mesmo dia (QA-04).
    final span = _daysBetween(edited.start, edited.end);
    return edited.copyWith(start: start, end: DateTime(start.year, start.month, start.day + span));
  }
  return edited.copyWith(start: start, end: start.add(edited.end.difference(edited.start)));
}

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

/// A partir desta largura (tablet, PC): mês e dia lado a lado, barra numa só linha e filtro de membros.
const wideBreakpoint = 840.0;

class _CalendarViewState extends State<CalendarView> {
  final _subscriptions = <StreamSubscription<Object?>>[];
  var _mode = CalendarMode.month;
  late DateTime _selected = DateUtils.dateOnly(widget.initialDate ?? clock.now());
  CalendarRange? _calendar;
  (DateTime, DateTime)? _loadedRange;
  HouseholdDetails? _details;
  Object? _error;

  /// Os membros escondidos pelo filtro (ecrã largo): os eventos só deles deixam de aparecer.
  final _hidden = <String>{};

  /// Só conta a resposta do pedido mais recente (ao navegar depressa, as anteriores chegam tarde).
  var _request = 0;

  /// As alterações que chegam em tempo real (de outra pessoa, ou uma sincronização do Google com 20 eventos) juntam-se
  /// numa só recarga do intervalo, sem voltar a pedir a casa (P2).
  late final _reload = CoalescedReload(() => _load(withDetails: false));

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
    _reload.dispose();
    super.dispose();
  }

  /// O intervalo à vista e (com [withDetails], ou se ainda não se tem) os membros da casa, para as cores e os nomes.
  Future<void> _load({bool withDetails = true}) async {
    final range = visibleRange(_mode, _selected);
    final request = ++_request;
    final needsDetails = withDetails || _details == null;
    try {
      final results = await Future.wait([
        _api.getCalendar(_householdId, range.$1, range.$2),
        if (needsDetails) _api.getHousehold(_householdId),
      ]);
      if (!mounted || request != _request) return;
      setState(() {
        _calendar = results[0] as CalendarRange;
        if (needsDetails) _details = results[1] as HouseholdDetails;
        _loadedRange = range;
        _error = null;
      });
    } catch (e) {
      if (mounted && request == _request) setState(() => _error = e);
    }
  }

  Future<void> _loadDetails() async {
    try {
      final details = await _api.getHousehold(_householdId);
      if (mounted) setState(() => _details = details);
    } catch (_) {
      // Ficam os nomes e as cores que já se tinham.
    }
  }

  void _onEvent(HouseholdEvent event) {
    switch (event) {
      case CalendarEventUpserted(event: final changed) when changed.householdId == _householdId:
        // Ignora versões que já temos (por exemplo, o eco das nossas próprias alterações).
        final known = _calendar?.events[changed.id];
        if (known == null || changed.updatedAt.isAfter(known.updatedAt)) _reload.request();
      case CalendarEventDeleted(:final eventId) when _calendar?.events.containsKey(eventId) ?? false:
        _reload.request();
      case CalendarEventsDeleted(:final eventIds) when eventIds.any((id) => _calendar?.events.containsKey(id) ?? false):
        _reload.request();
      // Os nomes e as cores vêm dos membros: só se voltam a pedir quando eles mudam.
      case MembersChanged(:final householdId) when householdId == _householdId:
        _loadDetails();
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
      if (member != null) return memberColor(context, member.color);
    }
    return Theme.of(context).colorScheme.primary;
  }

  /// Ex.: "Ana e Rui têm outro compromisso a essa hora".
  String _unavailable(Iterable<String> userIds) {
    final names = [for (final id in userIds) _details?.member(id)?.displayName ?? 'Alguém'.tr];
    final who = names.length == 1 ? names.single : '${names.take(names.length - 1).join(', ')} e ${names.last}'.tr;
    return '$who ${names.length == 1 ? 'tem'.tr : 'têm'.tr} outro compromisso a essa hora'.tr;
  }

  Future<void> _edit([CalendarEvent? event, CalendarOccurrence? occurrence]) async {
    final details = _details;
    if (details == null) return;
    final result = await showCalendarEventEditor(
      context,
      members: details.members,
      day: _selected,
      event: event,
      occurrence: occurrence,
    );
    if (result == null || !mounted) return;

    // Num evento que se repete, pergunta se a alteração é só desta ocorrência, das seguintes ou de todas.
    final repeats = event != null && occurrence != null && event.isRecurring;
    final scope = repeats ? await _askScope(deleting: result is CalendarEventDeleteRequested) : RecurringScope.all;
    if (scope == null || !mounted) return;

    try {
      switch (result) {
        case CalendarEventSaved(:final draft) when event == null:
          await _afterSave(await _api.createEvent(_householdId, draft));
        case CalendarEventSaved(:final draft) when !repeats || scope == RecurringScope.all:
          final series = repeats ? draftForSeries(draft, event, occurrence) : draft;
          await _afterSave(await _api.updateEvent(_householdId, event!.id, series));
        case CalendarEventSaved(:final draft):
          final following = scope == RecurringScope.following;
          final date = occurrence!.occurrenceDate;
          await _afterSave(await _api.updateOccurrence(_householdId, event!.id, date, draft, following: following));
        case CalendarEventDeleteRequested() when !repeats || scope == RecurringScope.all:
          await _api.deleteEvent(_householdId, event!.id);
          await _load();
        case CalendarEventDeleteRequested():
          final following = scope == RecurringScope.following;
          await _api.deleteOccurrence(_householdId, event!.id, occurrence!.occurrenceDate, following: following);
          await _load();
      }
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<RecurringScope?> _askScope({required bool deleting}) {
    return showDialog<RecurringScope>(
      context: context,
      builder: (context) => SimpleDialog(
        title: Text(deleting ? 'Apagar evento que se repete'.tr : 'Alterar evento que se repete'.tr),
        children: [
          for (final scope in RecurringScope.values)
            SimpleDialogOption(onPressed: () => Navigator.pop(context, scope), child: Text(scope.label)),
        ],
      ),
    );
  }

  /// Depois de guardar, abre o dia do evento e avisa se alguém não está disponível.
  Future<void> _afterSave(CalendarEvent saved) async {
    setState(() => _selected = DateUtils.dateOnly(saved.isAllDay ? saved.startDate! : saved.startsAt!));
    await _load();
    if (!mounted) return;
    final busy = _calendar?.conflictsFor(saved.id) ?? const <String>{};
    if (busy.isEmpty) return;
    // Um diálogo (não um SnackBar, que desaparece em 4 s): o conflito é para ler e decidir. Fica também no evento.
    await showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        icon: Icon(Icons.warning_rounded, color: context.kubico.warning),
        title: Text('Há um conflito'.tr),
        content: Text('${_unavailable(busy)}.'),
        actions: [FilledButton(onPressed: () => Navigator.pop(context), child: Text('Percebi'.tr))],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final calendar = _calendar;
    final canManage = widget.household.accessTo(HouseholdModule.calendar).canManage;

    return Scaffold(
      floatingActionButton: canManage
          ? FloatingActionButton.extended(
              onPressed: _edit,
              icon: const Icon(Icons.add_rounded),
              label: Text('Novo evento'.tr),
            )
          : null,
      body: Column(
        children: [
          _toolbar(context),
          if (_details != null && MediaQuery.sizeOf(context).width >= wideBreakpoint) _memberFilter(_details!),
          // Trocar de vista ou de intervalo é uma transição curta, não um salto (sem ela com menos movimento pedido).
          Expanded(
            child: AnimatedSwitcher(
              duration: MediaQuery.disableAnimationsOf(context) ? Duration.zero : const Duration(milliseconds: 200),
              child: KeyedSubtree(
                key: ValueKey((_mode, visibleRange(_mode, _selected))),
                child: calendar == null ? _placeholder() : _content(calendar),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _toolbar(BuildContext context) {
    final modes = SegmentedButton<CalendarMode>(
      segments: [for (final m in CalendarMode.values) ButtonSegment(value: m, label: Text(m.label))],
      selected: {_mode},
      showSelectedIcon: false,
      onSelectionChanged: (selection) => _go(_selected, selection.single),
    );
    final wide = MediaQuery.sizeOf(context).width >= wideBreakpoint;
    // Num ecrã largo, as vistas e a navegação numa só linha (no telemóvel, duas).
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 8, 8, 0),
      child: Flex(
        direction: wide ? Axis.horizontal : Axis.vertical,
        children: [
          if (wide) Flexible(child: modes) else modes,
          if (wide) const SizedBox(width: 16),
          _ExpandedIf(
            wide,
            child: Row(
              children: [
                IconButton(
                  tooltip: 'Anterior'.tr,
                  onPressed: () => _go(shiftFocus(_mode, _selected, -1)),
                  icon: const Icon(Icons.chevron_left_rounded),
                ),
                Expanded(
                  child: Text(
                    rangeTitle(_mode, _selected),
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                IconButton(
                  tooltip: 'Seguinte'.tr,
                  onPressed: () => _go(shiftFocus(_mode, _selected, 1)),
                  icon: const Icon(Icons.chevron_right_rounded),
                ),
                TextButton(onPressed: () => _go(clock.now()), child: Text('Hoje'.tr)),
                // As crianças veem o calendário só na app (o endereço funcionaria fora dela, sem sessão).
                if (widget.household.role != HouseholdRole.child)
                  IconButton(
                    tooltip: 'Ver noutros calendários'.tr,
                    onPressed: () async {
                      // O endereço subscrito e as ligações ao Google, Outlook e iCloud carregam-se só quando se abrem.
                      try {
                        await feed.loadLibrary();
                      } catch (e) {
                        if (context.mounted) showError(context, e);
                        return;
                      }
                      if (context.mounted) await feed.showCalendarFeedDialog(context, householdId: _householdId);
                    },
                    icon: const Icon(Icons.share_rounded),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _placeholder() {
    return _error == null
        ? const Center(child: CircularProgressIndicator())
        : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load);
  }

  /// O intervalo sem os eventos em que todos os participantes estão escondidos pelo filtro.
  CalendarRange _filtered(CalendarRange calendar) {
    if (_hidden.isEmpty) return calendar;
    bool visible(CalendarOccurrence o) {
      final people = calendar.events[o.eventId]?.participantUserIds ?? const [];
      return people.isEmpty || people.any((id) => !_hidden.contains(id));
    }

    return CalendarRange(
      events: calendar.events,
      occurrences: calendar.occurrences.where(visible).toList(),
      holidays: calendar.holidays,
    );
  }

  /// O filtro de membros (ecrã largo): um chip com o avatar de cada um, que liga e desliga os eventos dele.
  Widget _memberFilter(HouseholdDetails details) {
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      padding: const EdgeInsets.fromLTRB(12, 4, 12, 0),
      child: Row(
        children: [
          for (final m in details.members)
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: FilterChip(
                avatar: MemberAvatar(name: m.displayName, color: m.color, size: 24),
                label: Text(m.displayName),
                selected: !_hidden.contains(m.userId),
                showCheckmark: false,
                onSelected: (on) => setState(() => on ? _hidden.remove(m.userId) : _hidden.add(m.userId)),
              ),
            ),
        ],
      ),
    );
  }

  Widget _content(CalendarRange all) {
    final calendar = _filtered(all);
    final (first, last) = visibleRange(_mode, _selected);
    if (_mode == CalendarMode.week) {
      final canManage = widget.household.accessTo(HouseholdModule.calendar).canManage;
      return RefreshIndicator(
        onRefresh: _load,
        child: WeekTimeGrid(
          days: daysIn(first, last),
          calendar: calendar,
          colorFor: _colorFor,
          focusedDay: _selected,
          unavailable: _unavailable,
          onOpenDay: (day) => _go(day, CalendarMode.day),
          onTapEvent: canManage ? (event, occurrence) => _edit(event, occurrence) : null,
        ),
      );
    }
    final wide = MediaQuery.sizeOf(context).width >= wideBreakpoint;
    if (_mode == CalendarMode.month && wide) {
      // Num tablet ou PC, o mês à esquerda e a agenda do dia escolhido ao lado (em vez de por baixo).
      final dayRows = _daySection(calendar, _selected);
      return RefreshIndicator(
        onRefresh: _load,
        child: LayoutBuilder(
          builder: (context, constraints) => Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              SizedBox(
                width: (constraints.maxWidth * 0.5).clamp(420.0, 640.0),
                child: ListView(
                  padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
                  children: [
                    _MonthGrid(
                      start: first,
                      month: _selected.month,
                      selected: _selected,
                      calendar: calendar,
                      colorFor: _colorFor,
                      onSelect: _go,
                    ),
                  ],
                ),
              ),
              const VerticalDivider(width: 1),
              Expanded(
                child: ListView.builder(
                  padding: const EdgeInsets.fromLTRB(12, 0, 12, 96),
                  itemCount: dayRows.length,
                  itemBuilder: (context, index) => dayRows[index](context),
                ),
              ),
            ],
          ),
        ),
      );
    }
    final days = _mode == CalendarMode.month ? [_selected] : daysIn(first, last);
    // Na semana de uma casa cheia são centenas de ocorrências: só se constroem as que aparecem no ecrã (P10).
    final rows = <WidgetBuilder>[
      if (_mode == CalendarMode.month)
        (_) => _MonthGrid(
          start: first,
          month: _selected.month,
          selected: _selected,
          calendar: calendar,
          colorFor: _colorFor,
          onSelect: _go,
        ),
      for (final day in days) ..._daySection(calendar, day),
    ];
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView.builder(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
        itemCount: rows.length,
        itemBuilder: (context, index) => rows[index](context),
      ),
    );
  }

  List<WidgetBuilder> _daySection(CalendarRange calendar, DateTime day) {
    final theme = Theme.of(context);
    final holiday = calendar.holidayOn(day);
    final occurrences = calendar.on(day);
    return [
      (_) => Padding(
        padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
        child: Text(
          holiday == null ? formatLongDate(day) : '${formatLongDate(day)} · ${holiday.name}',
          style: theme.textTheme.titleSmall?.copyWith(color: holiday == null ? null : context.kubico.info),
        ),
      ),
      if (occurrences.isEmpty)
        (_) => Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          child: Text('Sem eventos.'.tr, style: TextStyle(color: theme.colorScheme.onSurfaceVariant)),
        ),
      for (final o in occurrences) (_) => _tile(o, calendar.events[o.eventId]!, day),
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
      onTap: widget.household.accessTo(HouseholdModule.calendar).canManage ? () => _edit(event, occurrence) : null,
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
    final today = DateUtils.dateOnly(clock.now());
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
      more: occurrences.length - 3,
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
    required this.more,
    required this.onTap,
  });

  final DateTime day;
  final bool inMonth;
  final bool isToday;
  final bool isSelected;
  final bool isHoliday;

  /// Cores dos primeiros eventos do dia.
  final List<Color> dots;

  /// Quantos eventos há além dos pontos (mostra-se "+N").
  final int more;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    // Feriado em cor de informação (não de erro); os dias de fora do mês em onSurfaceVariant (o outline falhava AA).
    final dayColor = isToday
        ? scheme.onPrimary
        : isHoliday
        ? context.kubico.info
        : (inMonth ? scheme.onSurface : scheme.onSurfaceVariant);
    return Padding(
      padding: const EdgeInsets.all(2),
      child: Material(
        color: isSelected ? scheme.primaryContainer : Colors.transparent,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12), side: BorderSide.none),
        child: InkWell(
          onTap: onTap,
          customBorder: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          // Altura mínima, não fixa: com letra grande a célula cresce em vez de cortar o número.
          child: ConstrainedBox(
            constraints: const BoxConstraints(minHeight: 56),
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  // O hoje num círculo cheio (não só um contorno), para se ver de relance.
                  Container(
                    padding: const EdgeInsets.all(4),
                    constraints: const BoxConstraints(minWidth: 28),
                    decoration: isToday ? BoxDecoration(color: scheme.primary, shape: BoxShape.circle) : null,
                    child: Text(
                      '${day.day}',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.labelLarge
                          ?.copyWith(color: dayColor, fontFeatures: tabularFigures),
                    ),
                  ),
                  const SizedBox(height: 2),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      for (final color in dots) _Dot(color: color),
                      if (more > 0)
                        Text(
                          '+$more',
                          style: Theme.of(context).textTheme.labelSmall
                              ?.copyWith(color: scheme.onSurfaceVariant, fontSize: 10, height: 1),
                        ),
                    ],
                  ),
                ],
              ),
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
    if (occurrence.isAllDay) return ('Todo o dia'.tr, null);
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
      if (event.dropOffUserId != null) 'Leva: ${_name(event.dropOffUserId)}'.tr,
      if (event.pickUpUserId != null) 'Vai buscar: ${_name(event.pickUpUserId)}'.tr,
    ].join(' · ');
    final participants = [for (final id in event.participantUserIds) details?.member(id)].whereType<Member>();

    // Sem ListTile (que limita a altura do que vai à esquerda): com letra grande, as horas, o título e o conflito
    // crescem em vez de cortar.
    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(KubicoRadius.md),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          child: IntrinsicHeight(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Container(
                  width: 4,
                  decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(2)),
                ),
                const SizedBox(width: 12),
                ConstrainedBox(
                  constraints: const BoxConstraints(minWidth: 52),
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Text(from, style: theme.textTheme.labelLarge?.copyWith(fontFeatures: tabularFigures)),
                      if (to != null)
                        Text(
                          to,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                            fontFeatures: tabularFigures,
                          ),
                        ),
                    ],
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Text(event.title, style: theme.textTheme.titleMedium),
                      if (subtitle.isNotEmpty)
                        Text(
                          subtitle,
                          style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                        ),
                      // O conflito vê-se sempre (não só numa tooltip, que não aparece com toque).
                      if (conflicts != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 2),
                          child: Row(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Icon(Icons.error_rounded, size: 18, color: context.kubico.warning),
                              const SizedBox(width: 4),
                              Expanded(
                                child: Text(
                                  conflicts!,
                                  style: theme.textTheme.bodySmall?.copyWith(color: context.kubico.onWarningContainer),
                                ),
                              ),
                            ],
                          ),
                        ),
                    ],
                  ),
                ),
                for (final member in participants.take(3))
                  Padding(
                    padding: const EdgeInsets.only(left: 4),
                    child: Center(
                      child: MemberAvatar(name: member.displayName, color: member.color, size: 24),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// [child] num Expanded quando [expand] (numa linha), ou sozinho (numa coluna sem altura limitada).
class _ExpandedIf extends StatelessWidget {
  const _ExpandedIf(this.expand, {required this.child});

  final bool expand;
  final Widget child;

  @override
  Widget build(BuildContext context) => expand ? Expanded(child: child) : child;
}
