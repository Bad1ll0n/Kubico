import 'package:flutter/material.dart';

import '../api/models.dart';
import 'theme.dart';

/// Resultado do editor: guardar (com os dados) ou apagar.
sealed class ChoreEditorResult {
  const ChoreEditorResult();
}

class ChoreSaved extends ChoreEditorResult {
  const ChoreSaved(this.draft);
  final ChoreDraft draft;
}

class ChoreDeleteRequested extends ChoreEditorResult {
  const ChoreDeleteRequested();
}

/// Abre o editor como folha inferior (telemóvel) ou diálogo (PC).
Future<ChoreEditorResult?> showChoreEditor(BuildContext context, {required List<Member> members, Chore? chore}) {
  final editor = _ChoreEditor(members: members, chore: chore);
  if (MediaQuery.sizeOf(context).width >= 700) {
    return showDialog<ChoreEditorResult>(
      context: context,
      builder: (_) => Dialog(
        child: ConstrainedBox(constraints: const BoxConstraints(maxWidth: 520), child: editor),
      ),
    );
  }
  return showModalBottomSheet<ChoreEditorResult>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (_) => editor,
  );
}

class _ChoreEditor extends StatefulWidget {
  const _ChoreEditor({required this.members, this.chore});

  final List<Member> members;
  final Chore? chore;

  @override
  State<_ChoreEditor> createState() => _ChoreEditorState();
}

class _ChoreEditorState extends State<_ChoreEditor> {
  late final TextEditingController _title;
  late final TextEditingController _notes;
  late int _points;
  late DateTime? _dueDate;
  late ChoreRecurrence _recurrence;
  late String? _assignee;
  late List<String> _rotation;
  late bool _rotates;

  bool get _isNew => widget.chore == null;

  @override
  void initState() {
    super.initState();
    final chore = widget.chore;
    _title = TextEditingController(text: chore?.title ?? '');
    _notes = TextEditingController(text: chore?.notes ?? '');
    _points = chore?.points ?? 1;
    _dueDate = chore?.dueDate;
    _recurrence = chore?.recurrence ?? ChoreRecurrence.none;
    _assignee = chore?.assigneeUserId;
    _rotation = [...?chore?.rotationUserIds];
    _rotates = _rotation.isNotEmpty;
  }

  @override
  void dispose() {
    _title.dispose();
    _notes.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _dueDate ?? now,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 5),
    );
    if (picked != null) setState(() => _dueDate = picked);
  }

  void _save() {
    final title = _title.text.trim();
    if (title.isEmpty) return;
    final rotation = _rotates && _recurrence != ChoreRecurrence.none ? _rotation : <String>[];
    Navigator.pop(
      context,
      ChoreSaved(
        ChoreDraft(
          title: title,
          notes: _notes.text.trim(),
          points: _points,
          dueDate: _dueDate,
          recurrence: _recurrence,
          // Com rotação, o servidor começa pelo primeiro da lista.
          assigneeUserId: rotation.isNotEmpty ? (rotation.contains(_assignee) ? _assignee : null) : _assignee,
          rotationUserIds: rotation,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final localizations = MaterialLocalizations.of(context);
    final canRotate = _recurrence != ChoreRecurrence.none && widget.members.length > 1;

    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(_isNew ? 'Nova tarefa' : 'Editar tarefa', style: theme.textTheme.titleLarge),
            const SizedBox(height: 16),
            TextField(
              controller: _title,
              autofocus: _isNew,
              textCapitalization: TextCapitalization.sentences,
              decoration: const InputDecoration(labelText: 'O que há para fazer?', hintText: 'Ex.: Tirar o lixo'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _notes,
              textCapitalization: TextCapitalization.sentences,
              decoration: const InputDecoration(labelText: 'Notas (opcional)'),
              maxLines: 2,
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: _pickDate,
                    icon: const Icon(Icons.event),
                    label: Text(_dueDate == null ? 'Sem prazo' : localizations.formatMediumDate(_dueDate!)),
                  ),
                ),
                if (_dueDate != null)
                  IconButton(
                    tooltip: 'Remover prazo',
                    onPressed: () => setState(() => _dueDate = null),
                    icon: const Icon(Icons.close),
                  ),
              ],
            ),
            const SizedBox(height: 12),
            DropdownButtonFormField<ChoreRecurrence>(
              initialValue: _recurrence,
              decoration: const InputDecoration(labelText: 'Repetição'),
              items: [for (final r in ChoreRecurrence.values) DropdownMenuItem(value: r, child: Text(r.label))],
              onChanged: (value) => setState(() => _recurrence = value ?? ChoreRecurrence.none),
            ),
            const SizedBox(height: 12),
            if (canRotate)
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Rodar entre membros'),
                subtitle: const Text('Cada vez que é feita, passa para a pessoa seguinte.'),
                value: _rotates,
                onChanged: (value) => setState(() {
                  _rotates = value;
                  if (value && _rotation.isEmpty) _rotation = [for (final m in widget.members) m.userId];
                }),
              ),
            if (canRotate && _rotates)
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final m in widget.members)
                    FilterChip(
                      avatar: CircleAvatar(backgroundColor: colorFromHex(m.color)),
                      label: Text(m.displayName),
                      selected: _rotation.contains(m.userId),
                      onSelected: (selected) => setState(() {
                        selected ? _rotation.add(m.userId) : _rotation.remove(m.userId);
                      }),
                    ),
                ],
              )
            else
              DropdownButtonFormField<String?>(
                initialValue: widget.members.any((m) => m.userId == _assignee) ? _assignee : null,
                decoration: const InputDecoration(labelText: 'Responsável'),
                items: [
                  const DropdownMenuItem<String?>(value: null, child: Text('Qualquer pessoa')),
                  for (final m in widget.members)
                    DropdownMenuItem<String?>(value: m.userId, child: Text(m.displayName)),
                ],
                onChanged: (value) => setState(() => _assignee = value),
              ),
            const SizedBox(height: 16),
            Row(
              children: [
                Text('Pontos', style: theme.textTheme.titleSmall),
                Expanded(
                  child: Slider(
                    value: _points.toDouble(),
                    min: 0,
                    max: 20,
                    divisions: 20,
                    label: '$_points',
                    onChanged: (v) => setState(() => _points = v.round()),
                  ),
                ),
                SizedBox(width: 28, child: Text('$_points', textAlign: TextAlign.end)),
              ],
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                if (!_isNew)
                  TextButton.icon(
                    onPressed: () => Navigator.pop(context, const ChoreDeleteRequested()),
                    icon: Icon(Icons.delete_outline, color: theme.colorScheme.error),
                    label: Text('Apagar', style: TextStyle(color: theme.colorScheme.error)),
                  ),
                const Spacer(),
                TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
                const SizedBox(width: 8),
                ValueListenableBuilder(
                  valueListenable: _title,
                  builder: (context, value, _) => FilledButton(
                    onPressed: value.text.trim().isEmpty || (_rotates && canRotate && _rotation.length < 2)
                        ? null
                        : _save,
                    child: const Text('Guardar'),
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
