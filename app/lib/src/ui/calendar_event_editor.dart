import 'package:flutter/material.dart';

import '../api/models.dart';
import '../l10n/strings.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// Resultado do editor: guardar (com os dados) ou apagar.
sealed class CalendarEventEditorResult {
  const CalendarEventEditorResult();
}

class CalendarEventSaved extends CalendarEventEditorResult {
  const CalendarEventSaved(this.draft);
  final CalendarEventDraft draft;
}

class CalendarEventDeleteRequested extends CalendarEventEditorResult {
  const CalendarEventDeleteRequested();
}

/// Abre o editor como folha inferior (telemóvel) ou diálogo (PC). Um evento novo começa em [day]; num evento que
/// se repete, o editor mostra as datas da [occurrence] escolhida.
Future<CalendarEventEditorResult?> showCalendarEventEditor(
  BuildContext context, {
  required List<Member> members,
  required DateTime day,
  CalendarEvent? event,
  CalendarOccurrence? occurrence,
}) {
  final editor = _CalendarEventEditor(members: members, day: day, event: event, occurrence: occurrence);
  if (MediaQuery.sizeOf(context).width >= 700) {
    return showDialog<CalendarEventEditorResult>(
      context: context,
      builder: (_) => Dialog(
        child: ConstrainedBox(constraints: const BoxConstraints(maxWidth: 560), child: editor),
      ),
    );
  }
  return showModalBottomSheet<CalendarEventEditorResult>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (_) => editor,
  );
}

class _CalendarEventEditor extends StatefulWidget {
  const _CalendarEventEditor({required this.members, required this.day, this.event, this.occurrence});

  final List<Member> members;
  final DateTime day;
  final CalendarEvent? event;
  final CalendarOccurrence? occurrence;

  @override
  State<_CalendarEventEditor> createState() => _CalendarEventEditorState();
}

class _CalendarEventEditorState extends State<_CalendarEventEditor> {
  late final TextEditingController _title;
  late final TextEditingController _location;
  late final TextEditingController _notes;
  late bool _allDay;
  late DateTime _start;
  late DateTime _end;
  RecurrenceFrequency? _frequency;
  var _interval = 1;
  int? _count;
  DateTime? _until;
  var _weekdays = <int>[];
  var _participants = <String>[];
  String? _dropOff;
  String? _pickUp;

  bool get _isNew => widget.event == null;

  /// Uma alteração a um só dia de uma série não se repete.
  bool get _canRepeat => widget.event?.seriesId == null;

  @override
  void initState() {
    super.initState();
    final event = widget.event;
    _title = TextEditingController(text: event?.title ?? '');
    _location = TextEditingController(text: event?.location ?? '');
    _notes = TextEditingController(text: event?.notes ?? '');

    if (event == null) {
      _allDay = false;
      _start = _defaultStart(widget.day);
      _end = _start.add(const Duration(hours: 1));
    } else {
      final draft = CalendarEventDraft.from(event);
      final occurrence = widget.occurrence;
      _allDay = draft.isAllDay;
      _start = draft.start;
      _end = draft.end;
      if (occurrence != null && event.isRecurring) {
        _start = (occurrence.startDate ?? occurrence.startsAt)!;
        _end = (occurrence.endDate ?? occurrence.endsAt)!;
      }
    }

    final recurrence = Recurrence.parse(event?.recurrence);
    _frequency = recurrence?.frequency;
    _interval = recurrence?.interval ?? 1;
    _count = recurrence?.count;
    _until = recurrence?.until;
    _weekdays = [...?recurrence?.weekdays];
    _participants = [...?event?.participantUserIds];
    _dropOff = event?.dropOffUserId;
    _pickUp = event?.pickUpUserId;
  }

  @override
  void dispose() {
    _title.dispose();
    _location.dispose();
    _notes.dispose();
    super.dispose();
  }

  /// Um evento novo começa à próxima hora certa (se for hoje) ou às 9:00.
  static DateTime _defaultStart(DateTime day) {
    final now = DateTime.now();
    if (DateUtils.isSameDay(day, now)) return DateTime(now.year, now.month, now.day, now.hour + 1);
    return DateTime(day.year, day.month, day.day, 9);
  }

  /// Dias de calendário entre duas datas, sem contar com a mudança de hora.
  static int _daysBetween(DateTime a, DateTime b) =>
      DateTime.utc(b.year, b.month, b.day).difference(DateTime.utc(a.year, a.month, a.day)).inDays;

  String? get _dateError {
    if (_allDay) return _end.isBefore(_start) ? 'O último dia não pode ser antes do primeiro.'.tr : null;
    return _end.isAfter(_start) ? null : 'O fim tem de ser depois do início.'.tr;
  }

  Future<DateTime?> _pickDate(DateTime initial) => showDatePicker(
    context: context,
    initialDate: initial,
    firstDate: DateTime(initial.year - 5),
    lastDate: DateTime(initial.year + 10),
  );

  Future<TimeOfDay?> _pickTime(DateTime initial) =>
      showTimePicker(context: context, initialTime: TimeOfDay.fromDateTime(initial));

  Future<void> _pickStart({required bool time}) async {
    final date = time ? _start : await _pickDate(_start);
    final clock = time ? await _pickTime(_start) : TimeOfDay.fromDateTime(_start);
    if (date == null || clock == null || !mounted) return;
    setState(() {
      final duration = _end.difference(_start);
      final days = _daysBetween(_start, _end);
      _start = DateTime(date.year, date.month, date.day, clock.hour, clock.minute);
      // Mudar o início arrasta o fim, para o evento manter a duração.
      _end = _allDay ? DateTime(_start.year, _start.month, _start.day + days) : _start.add(duration);
    });
  }

  Future<void> _pickEnd({required bool time}) async {
    final date = time ? _end : await _pickDate(_end);
    final clock = time ? await _pickTime(_end) : TimeOfDay.fromDateTime(_end);
    if (date == null || clock == null || !mounted) return;
    setState(() => _end = DateTime(date.year, date.month, date.day, clock.hour, clock.minute));
  }

  Future<void> _pickUntil() async {
    final picked = await _pickDate(_until ?? _start);
    if (picked == null || !mounted) return;
    setState(() {
      _until = picked;
      _count = null;
    });
  }

  void _setAllDay(bool value) {
    setState(() {
      _allDay = value;
      if (value) {
        _start = DateUtils.dateOnly(_start);
        _end = DateUtils.dateOnly(_end);
      } else {
        _start = _defaultStart(_start);
        _end = _start.add(const Duration(hours: 1));
      }
    });
  }

  void _setFrequency(RecurrenceFrequency? value) {
    if (value == _frequency) return;
    setState(() {
      _frequency = value;
      _interval = 1;
      _count = null;
      // Por omissão, repete-se no dia da semana em que começa.
      if (value == RecurrenceFrequency.weekly && _weekdays.isEmpty) _weekdays = [_start.weekday];
    });
  }

  Recurrence? _recurrence() {
    final frequency = _frequency;
    if (frequency == null || !_canRepeat) return null;
    final weekdays = frequency == RecurrenceFrequency.weekly ? _weekdays : const <int>[];
    return Recurrence(frequency, interval: _interval, weekdays: weekdays, count: _count, until: _until);
  }

  void _save() {
    final draft = CalendarEventDraft(
      title: _title.text.trim(),
      start: _start,
      end: _end,
      isAllDay: _allDay,
      location: _location.text.trim(),
      notes: _notes.text.trim(),
      recurrence: _recurrence()?.toRule(),
      participantUserIds: _participants,
      // As boleias só fazem sentido em eventos com hora.
      dropOffUserId: _allDay ? null : _dropOff,
      pickUpUserId: _allDay ? null : _pickUp,
    );
    Navigator.pop(context, CalendarEventSaved(draft));
  }

  Widget _rideField(String label, String? value, ValueChanged<String?> onChanged) {
    // As crianças não levam nem vão buscar ninguém.
    final drivers = widget.members.where((m) => m.role != HouseholdRole.child).toList();
    return DropdownButtonFormField<String?>(
      key: ValueKey(label),
      initialValue: drivers.any((m) => m.userId == value) ? value : null,
      decoration: InputDecoration(labelText: label, prefixIcon: const Icon(Icons.directions_car_outlined)),
      items: [
        DropdownMenuItem<String?>(value: null, child: Text('Ninguém'.tr)),
        for (final m in drivers) DropdownMenuItem<String?>(value: m.userId, child: Text(m.displayName)),
      ],
      onChanged: onChanged,
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(_isNew ? 'Novo evento'.tr : 'Editar evento'.tr, style: theme.textTheme.titleLarge),
            const SizedBox(height: 16),
            TextField(
              controller: _title,
              autofocus: _isNew,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'O que é?'.tr, hintText: 'Ex.: Natação da Rita'.tr),
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: Text('Todo o dia'.tr),
              value: _allDay,
              onChanged: _setAllDay,
            ),
            _DateTimeRow(
              label: _allDay ? 'Primeiro dia'.tr : 'Início'.tr,
              value: _start,
              showTime: !_allDay,
              onPickDate: () => _pickStart(time: false),
              onPickTime: () => _pickStart(time: true),
            ),
            const SizedBox(height: 8),
            _DateTimeRow(
              label: _allDay ? 'Último dia'.tr : 'Fim'.tr,
              value: _end,
              showTime: !_allDay,
              onPickDate: () => _pickEnd(time: false),
              onPickTime: () => _pickEnd(time: true),
            ),
            if (_dateError case final error?)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(error, style: TextStyle(color: theme.colorScheme.error)),
              ),
            const SizedBox(height: 16),
            TextField(
              controller: _location,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'Local (opcional)'.tr),
            ),
            if (_canRepeat) ...[
              const SizedBox(height: 12),
              DropdownButtonFormField<RecurrenceFrequency?>(
                initialValue: _frequency,
                decoration: InputDecoration(labelText: 'Repetição'.tr),
                items: [
                  DropdownMenuItem<RecurrenceFrequency?>(value: null, child: Text('Não se repete'.tr)),
                  for (final f in RecurrenceFrequency.values) DropdownMenuItem(value: f, child: Text(f.label)),
                ],
                onChanged: _setFrequency,
              ),
            ],
            if (_canRepeat && _frequency == RecurrenceFrequency.weekly) ...[
              const SizedBox(height: 12),
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  for (var day = DateTime.monday; day <= DateTime.sunday; day++)
                    FilterChip(
                      label: Text(weekdayShortNames[day - 1]),
                      selected: _weekdays.contains(day),
                      onSelected: (selected) => setState(() {
                        selected ? _weekdays.add(day) : _weekdays.remove(day);
                      }),
                    ),
                ],
              ),
            ],
            if (_canRepeat && _frequency != null)
              Row(
                children: [
                  Expanded(
                    child: TextButton.icon(
                      onPressed: _pickUntil,
                      icon: const Icon(Icons.event_repeat),
                      label: Text(_until == null ? 'Repete-se sem fim'.tr : 'Até ${formatShortDate(_until!)}'.tr),
                    ),
                  ),
                  if (_until != null)
                    IconButton(
                      tooltip: 'Repetir sem fim'.tr,
                      onPressed: () => setState(() => _until = null),
                      icon: const Icon(Icons.close),
                    ),
                ],
              ),
            const SizedBox(height: 16),
            Text('Quem participa'.tr, style: theme.textTheme.titleSmall),
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final m in widget.members)
                  FilterChip(
                    avatar: CircleAvatar(backgroundColor: colorFromHex(m.color)),
                    label: Text(m.displayName),
                    selected: _participants.contains(m.userId),
                    onSelected: (selected) => setState(() {
                      selected ? _participants.add(m.userId) : _participants.remove(m.userId);
                    }),
                  ),
              ],
            ),
            if (!_allDay) ...[
              const SizedBox(height: 16),
              _rideField('Quem leva'.tr, _dropOff, (value) => setState(() => _dropOff = value)),
              const SizedBox(height: 12),
              _rideField('Quem vai buscar'.tr, _pickUp, (value) => setState(() => _pickUp = value)),
            ],
            const SizedBox(height: 12),
            TextField(
              controller: _notes,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'Notas (opcional)'.tr),
              maxLines: 2,
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                if (!_isNew)
                  TextButton.icon(
                    onPressed: () => Navigator.pop(context, const CalendarEventDeleteRequested()),
                    icon: Icon(Icons.delete_outline, color: theme.colorScheme.error),
                    label: Text('Apagar'.tr, style: TextStyle(color: theme.colorScheme.error)),
                  ),
                const Spacer(),
                TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
                const SizedBox(width: 8),
                ValueListenableBuilder(
                  valueListenable: _title,
                  builder: (context, value, _) => FilledButton(
                    onPressed: value.text.trim().isEmpty || _dateError != null ? null : _save,
                    child: Text('Guardar'.tr),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// Uma linha com o dia (e a hora, se [showTime]) do início ou do fim.
class _DateTimeRow extends StatelessWidget {
  const _DateTimeRow({
    required this.label,
    required this.value,
    required this.showTime,
    required this.onPickDate,
    required this.onPickTime,
  });

  final String label;
  final DateTime value;
  final bool showTime;
  final VoidCallback onPickDate;
  final VoidCallback onPickTime;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        SizedBox(width: 96, child: Text(label)),
        Expanded(
          child: OutlinedButton(onPressed: onPickDate, child: Text(formatShortDate(value))),
        ),
        if (showTime) ...[
          const SizedBox(width: 8),
          OutlinedButton(onPressed: onPickTime, child: Text(formatTime(value))),
        ],
      ],
    );
  }
}
