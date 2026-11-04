import 'dart:math' as math;

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';

import '../api/models.dart';
import '../l10n/strings.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// A semana em grelha de horas (07, DES calendário): uma coluna por dia, os eventos com hora em blocos posicionados
/// pela hora (os que se sobrepõem lado a lado) e os de dia inteiro numa faixa em cima. Mostra das 7 às 22 h, ou mais
/// se houver eventos fora disso. Num ecrã estreito vê-se 3 dias de cada vez, com deslize horizontal. As horas
/// crescem com a letra (a 200 % uma hora tem o dobro da altura).
class WeekTimeGrid extends StatefulWidget {
  const WeekTimeGrid({
    required this.days,
    required this.calendar,
    required this.colorFor,
    required this.onOpenDay,
    this.focusedDay,
    this.onTapEvent,
    this.unavailable,
    super.key,
  });

  final List<DateTime> days;
  final CalendarRange calendar;
  final Color Function(CalendarEvent event) colorFor;

  /// Tocar no cabeçalho de um dia abre-o (vista de dia).
  final ValueChanged<DateTime> onOpenDay;

  /// O dia a mostrar primeiro quando não cabem todos.
  final DateTime? focusedDay;

  /// Tocar num evento (só para quem gere o calendário; para os outros abre o dia).
  final void Function(CalendarEvent event, CalendarOccurrence occurrence)? onTapEvent;

  /// O texto do conflito ("Ana tem outro compromisso a essa hora").
  final String Function(Iterable<String> userIds)? unavailable;

  static const firstHour = 7;
  static const lastHour = 22;

  /// As horas a mostrar: das 7 às 22, alargadas para caber o que houver nessa semana.
  static (int, int) hoursFor(List<DateTime> days, CalendarRange calendar) {
    var first = firstHour;
    var last = lastHour;
    for (final day in days) {
      for (final o in calendar.on(day)) {
        if (o.isAllDay) continue;
        final (start, end) = _clip(o, day);
        first = math.min(first, start.hour);
        final endHour = end.difference(DateTime(day.year, day.month, day.day)).inMinutes / 60;
        last = math.max(last, endHour.ceil());
      }
    }
    return (first, math.min(last, 24));
  }

  @override
  State<WeekTimeGrid> createState() => _WeekTimeGridState();
}

/// O início e o fim de uma ocorrência dentro de [day] (um evento que passa a meia-noite fica cortado).
(DateTime, DateTime) _clip(CalendarOccurrence o, DateTime day) {
  final dayStart = DateTime(day.year, day.month, day.day);
  final dayEnd = DateTime(day.year, day.month, day.day + 1);
  final start = o.startsAt!.isBefore(dayStart) ? dayStart : o.startsAt!;
  final end = o.endsAt!.isAfter(dayEnd) ? dayEnd : o.endsAt!;
  return (start, end.isAfter(start) ? end : start);
}

/// Distribui os eventos de um dia por colunas: os que se sobrepõem ficam lado a lado, cada grupo com a largura
/// dividida pelo número de colunas de que precisa.
List<({CalendarOccurrence occurrence, DateTime start, DateTime end, int column, int columns})> layoutDay(
  List<CalendarOccurrence> occurrences,
  DateTime day,
) {
  final timed = [
    for (final o in occurrences)
      if (!o.isAllDay) (occurrence: o, range: _clip(o, day)),
  ]..sort((a, b) => a.range.$1.compareTo(b.range.$1));
  final result = <({CalendarOccurrence occurrence, DateTime start, DateTime end, int column, int columns})>[];
  var group = <({CalendarOccurrence occurrence, (DateTime, DateTime) range, int column})>[];
  var columnEnds = <DateTime>[];
  DateTime? groupEnd;

  void close() {
    for (final g in group) {
      result.add((
        occurrence: g.occurrence,
        start: g.range.$1,
        end: g.range.$2,
        column: g.column,
        columns: columnEnds.length,
      ));
    }
    group = [];
    columnEnds = [];
    groupEnd = null;
  }

  for (final t in timed) {
    final (start, end) = t.range;
    if (groupEnd != null && !start.isBefore(groupEnd!)) close();
    // A primeira coluna já livre a esta hora; senão, uma nova.
    var column = columnEnds.indexWhere((e) => !e.isAfter(start));
    if (column < 0) {
      column = columnEnds.length;
      columnEnds.add(end);
    } else {
      columnEnds[column] = end;
    }
    group.add((occurrence: t.occurrence, range: t.range, column: column));
    if (groupEnd == null || end.isAfter(groupEnd!)) groupEnd = end;
  }
  close();
  return result;
}

class _WeekTimeGridState extends State<WeekTimeGrid> {
  final _body = ScrollController();
  final _header = ScrollController();
  final _vertical = ScrollController();
  var _scrolledToFocus = false;

  @override
  void initState() {
    super.initState();
    // O cabeçalho (dias e faixa de dia inteiro) acompanha o deslize da grelha.
    _body.addListener(() {
      if (_header.hasClients && _header.offset != _body.offset) _header.jumpTo(_body.offset);
    });
  }

  @override
  void didUpdateWidget(WeekTimeGrid old) {
    super.didUpdateWidget(old);
    if (old.days.first != widget.days.first) _scrolledToFocus = false;
  }

  @override
  void dispose() {
    _body.dispose();
    _header.dispose();
    _vertical.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scale = MediaQuery.textScalerOf(context).scale(1).clamp(1.0, 2.0);
    final hourHeight = 52.0 * scale;
    final gutter = 44.0 * scale;
    final (firstHour, lastHour) = WeekTimeGrid.hoursFor(widget.days, widget.calendar);
    final today = DateUtils.dateOnly(clock.now());

    return LayoutBuilder(
      builder: (context, constraints) {
        final available = constraints.maxWidth - gutter;
        // A semana toda quando cabe; senão os dias que cabem com pelo menos 100 px (a crescer com a letra), nunca
        // menos de 2.
        final visible = available >= 700 ? 7 : (available ~/ (100 * scale)).clamp(2, 7);
        final columnWidth = available / visible;

        // Ao abrir a semana num ecrã estreito, mostra-se o dia escolhido (ou hoje) e, em cima, as 7 h ou o primeiro
        // evento.
        if (!_scrolledToFocus) {
          _scrolledToFocus = true;
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (!mounted || !_body.hasClients) return;
            final focus = widget.focusedDay ?? today;
            final index = widget.days.indexWhere((d) => DateUtils.isSameDay(d, focus));
            if (index > 0 && visible < widget.days.length) {
              _body.jumpTo(math.min(index * columnWidth, _body.position.maxScrollExtent));
            }
          });
        }

        Widget dayColumns(Widget Function(DateTime day) build) => Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [for (final day in widget.days) SizedBox(width: columnWidth, child: build(day))],
        );

        return Column(
          children: [
            // Os dias e os eventos de dia inteiro.
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(width: gutter),
                Expanded(
                  child: SingleChildScrollView(
                    controller: _header,
                    scrollDirection: Axis.horizontal,
                    physics: const NeverScrollableScrollPhysics(),
                    child: IntrinsicHeight(
                      child: dayColumns(
                        (day) => _DayHeader(
                          day: day,
                          today: DateUtils.isSameDay(day, today),
                          holiday: widget.calendar.holidayOn(day),
                          allDay: [
                            for (final o in widget.calendar.on(day))
                              if (o.isAllDay) (o, widget.calendar.events[o.eventId]!),
                          ],
                          colorFor: widget.colorFor,
                          onOpen: () => widget.onOpenDay(day),
                          onTapEvent: widget.onTapEvent,
                        ),
                      ),
                    ),
                  ),
                ),
              ],
            ),
            Divider(height: 1, color: theme.colorScheme.outlineVariant),
            Expanded(
              child: SingleChildScrollView(
                controller: _vertical,
                padding: const EdgeInsets.only(bottom: 96),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    SizedBox(
                      width: gutter,
                      child: Column(
                        children: [
                          for (var h = firstHour; h < lastHour; h++)
                            SizedBox(
                              height: hourHeight,
                              child: Align(
                                alignment: Alignment.topRight,
                                child: Padding(
                                  padding: const EdgeInsets.only(right: 6),
                                  child: Text(
                                    '${h.toString().padLeft(2, '0')}h',
                                    style: theme.textTheme.labelSmall?.copyWith(
                                      color: theme.colorScheme.onSurfaceVariant,
                                      fontFeatures: tabularFigures,
                                    ),
                                  ),
                                ),
                              ),
                            ),
                        ],
                      ),
                    ),
                    Expanded(
                      child: SingleChildScrollView(
                        controller: _body,
                        scrollDirection: Axis.horizontal,
                        child: dayColumns(
                          (day) => _DayColumn(
                            day: day,
                            today: DateUtils.isSameDay(day, today),
                            firstHour: firstHour,
                            lastHour: lastHour,
                            hourHeight: hourHeight,
                            width: columnWidth,
                            calendar: widget.calendar,
                            colorFor: widget.colorFor,
                            unavailable: widget.unavailable,
                            onTap: (event, occurrence) => widget.onTapEvent != null
                                ? widget.onTapEvent!(event, occurrence)
                                : widget.onOpenDay(day),
                          ),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ],
        );
      },
    );
  }
}

class _DayHeader extends StatelessWidget {
  const _DayHeader({
    required this.day,
    required this.today,
    required this.holiday,
    required this.allDay,
    required this.colorFor,
    required this.onOpen,
    required this.onTapEvent,
  });

  final DateTime day;
  final bool today;
  final Holiday? holiday;
  final List<(CalendarOccurrence, CalendarEvent)> allDay;
  final Color Function(CalendarEvent event) colorFor;
  final VoidCallback onOpen;
  final void Function(CalendarEvent event, CalendarOccurrence occurrence)? onTapEvent;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 2),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            button: true,
            label: [formatLongDate(day), if (today) 'hoje'.tr, if (holiday != null) holiday!.name].join(', '),
            excludeSemantics: true,
            child: InkWell(
              key: ValueKey('week-day-${CalendarRange.dayKey(day)}'),
              onTap: onOpen,
              borderRadius: BorderRadius.circular(KubicoRadius.sm),
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 6),
                child: Column(
                  children: [
                    Text(
                      weekdayShortNames[day.weekday - 1],
                      style: theme.textTheme.labelMedium?.copyWith(color: scheme.onSurfaceVariant),
                    ),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                      decoration: today
                          ? BoxDecoration(color: scheme.primary, borderRadius: BorderRadius.circular(999))
                          : null,
                      child: Text(
                        '${day.day}',
                        style: theme.textTheme.titleMedium?.copyWith(
                          fontFeatures: tabularFigures,
                          color: today ? scheme.onPrimary : null,
                        ),
                      ),
                    ),
                    if (holiday != null)
                      Text(
                        holiday!.name,
                        textAlign: TextAlign.center,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.labelSmall?.copyWith(color: context.kubico.info),
                      ),
                  ],
                ),
              ),
            ),
          ),
          for (final (occurrence, event) in allDay)
            Padding(
              padding: const EdgeInsets.only(bottom: 2),
              child: _Chip(
                event: event,
                color: colorFor(event),
                onTap: onTapEvent == null ? onOpen : () => onTapEvent!(event, occurrence),
              ),
            ),
          const SizedBox(height: 4),
        ],
      ),
    );
  }
}

/// Um evento de dia inteiro na faixa de cima.
class _Chip extends StatelessWidget {
  const _Chip({required this.event, required this.color, required this.onTap});

  final CalendarEvent event;
  final Color color;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Material(
      color: Color.alphaBlend(color.withValues(alpha: 0.2), theme.colorScheme.surface),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(KubicoRadius.xs),
        side: BorderSide(color: color, width: 1),
      ),
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
          child: Text(
            event.title,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.labelMedium?.copyWith(color: theme.colorScheme.onSurface),
          ),
        ),
      ),
    );
  }
}

class _DayColumn extends StatelessWidget {
  const _DayColumn({
    required this.day,
    required this.today,
    required this.firstHour,
    required this.lastHour,
    required this.hourHeight,
    required this.width,
    required this.calendar,
    required this.colorFor,
    required this.unavailable,
    required this.onTap,
  });

  final DateTime day;
  final bool today;
  final int firstHour;
  final int lastHour;
  final double hourHeight;
  final double width;
  final CalendarRange calendar;
  final Color Function(CalendarEvent event) colorFor;
  final String Function(Iterable<String> userIds)? unavailable;
  final void Function(CalendarEvent event, CalendarOccurrence occurrence) onTap;

  double _y(DateTime t) {
    final minutes = t.difference(DateTime(day.year, day.month, day.day)).inMinutes - firstHour * 60;
    return minutes / 60 * hourHeight;
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final height = (lastHour - firstHour) * hourHeight;
    final now = clock.now();
    final minBlock = hourHeight * 0.45;
    return Container(
      height: height,
      decoration: BoxDecoration(
        color: today ? scheme.primary.withValues(alpha: 0.05) : null,
        border: Border(left: BorderSide(color: scheme.outlineVariant)),
      ),
      child: Stack(
        children: [
          for (var h = 1; h < lastHour - firstHour; h++)
            Positioned(
              top: h * hourHeight,
              left: 0,
              right: 0,
              child: Divider(height: 1, color: scheme.outlineVariant.withValues(alpha: 0.6)),
            ),
          for (final block in layoutDay(calendar.on(day), day))
            Positioned(
              top: _y(block.start),
              height: math.max(_y(block.end) - _y(block.start), minBlock),
              left: 2 + (width - 4) / block.columns * block.column,
              width: (width - 4) / block.columns - 2,
              child: _Block(
                occurrence: block.occurrence,
                event: calendar.events[block.occurrence.eventId]!,
                start: block.start,
                end: block.end,
                color: colorFor(calendar.events[block.occurrence.eventId]!),
                conflict: block.occurrence.conflictUserIds.isEmpty || unavailable == null
                    ? null
                    : unavailable!(block.occurrence.conflictUserIds),
                onTap: () => onTap(calendar.events[block.occurrence.eventId]!, block.occurrence),
              ),
            ),
          // A linha de agora.
          if (today && now.hour >= firstHour && now.hour < lastHour)
            Positioned(
              top: _y(now),
              left: 0,
              right: 0,
              child: ExcludeSemantics(child: Container(height: 2, color: scheme.primary)),
            ),
        ],
      ),
    );
  }
}

/// Um evento com hora: fundo suave na cor de quem participa, a barra dessa cor, o título e as horas. Num bloco curto
/// o texto corta-se, mas o leitor de ecrã lê tudo (e tocar abre o evento ou o dia).
class _Block extends StatelessWidget {
  const _Block({
    required this.occurrence,
    required this.event,
    required this.start,
    required this.end,
    required this.color,
    required this.conflict,
    required this.onTap,
  });

  final CalendarOccurrence occurrence;
  final CalendarEvent event;
  final DateTime start;
  final DateTime end;
  final Color color;
  final String? conflict;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final warning = context.kubico.warning;
    final times = '${formatTime(start)}–${formatTime(end)}';
    final scale = MediaQuery.textScalerOf(context).scale(1);
    return Semantics(
      button: true,
      label: [event.title, times, if (event.location != null) event.location!, ?conflict].join(', '),
      excludeSemantics: true,
      child: Material(
        color: Color.alphaBlend(color.withValues(alpha: 0.2), theme.colorScheme.surface),
        borderRadius: BorderRadius.circular(KubicoRadius.xs),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Container(
            decoration: BoxDecoration(
              border: Border(left: BorderSide(color: conflict == null ? color : warning, width: 4)),
            ),
            padding: const EdgeInsets.fromLTRB(4, 2, 2, 2),
            // Num bloco curto o texto fica cortado em baixo, sem erro de transbordo.
            child: LayoutBuilder(
              builder: (context, constraints) {
                final narrow = constraints.maxWidth < 64 * scale;
                return SingleChildScrollView(
                  physics: const NeverScrollableScrollPhysics(),
                  primary: false,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          if (conflict != null) ...[
                            Icon(Icons.error_rounded, size: 14, color: warning),
                            const SizedBox(width: 2),
                          ],
                          Expanded(
                            child: Text(
                              event.title,
                              // Num bloco estreito, uma linha com reticências (em vez de partir a palavra a meio).
                              maxLines: narrow ? 1 : 2,
                              overflow: TextOverflow.ellipsis,
                              style: theme.textTheme.labelMedium?.copyWith(
                                fontWeight: FontWeight.w700,
                                color: theme.colorScheme.onSurface,
                              ),
                            ),
                          ),
                        ],
                      ),
                      Text(
                        // Só a hora de início (a de fim vê-se pela altura do bloco e lê-se no leitor de ecrã).
                        formatTime(start),
                        maxLines: 1,
                        overflow: TextOverflow.clip,
                        style: theme.textTheme.labelSmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                          fontFeatures: tabularFigures,
                        ),
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ),
      ),
    );
  }
}
