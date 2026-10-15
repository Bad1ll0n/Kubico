import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../api/models.dart';

/// Resultado do editor: guardar (com os dados) ou apagar.
sealed class RewardEditorResult {
  const RewardEditorResult();
}

class RewardSaved extends RewardEditorResult {
  const RewardSaved(this.draft);
  final RewardDraft draft;
}

class RewardDeleteRequested extends RewardEditorResult {
  const RewardDeleteRequested();
}

Future<RewardEditorResult?> showRewardEditor(BuildContext context, {Reward? reward}) => showDialog<RewardEditorResult>(
  context: context,
  builder: (_) => _RewardEditor(reward: reward),
);

class _RewardEditor extends StatefulWidget {
  const _RewardEditor({this.reward});

  final Reward? reward;

  @override
  State<_RewardEditor> createState() => _RewardEditorState();
}

class _RewardEditorState extends State<_RewardEditor> {
  late final _title = TextEditingController(text: widget.reward?.title ?? '');
  late final _description = TextEditingController(text: widget.reward?.description ?? '');
  late final _cost = TextEditingController(text: '${widget.reward?.cost ?? 20}');

  bool get _isNew => widget.reward == null;

  int? get _costValue {
    final value = int.tryParse(_cost.text);
    return value != null && value >= 1 && value <= 100000 ? value : null;
  }

  @override
  void dispose() {
    _title.dispose();
    _description.dispose();
    _cost.dispose();
    super.dispose();
  }

  void _save() {
    final draft = RewardDraft(title: _title.text.trim(), description: _description.text.trim(), cost: _costValue!);
    Navigator.pop(context, RewardSaved(draft));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: Text(_isNew ? 'Nova recompensa' : 'Editar recompensa'),
      content: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: _title,
              autofocus: _isNew,
              textCapitalization: TextCapitalization.sentences,
              decoration: const InputDecoration(labelText: 'Recompensa', hintText: 'Ex.: Escolher o filme de sexta'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _description,
              textCapitalization: TextCapitalization.sentences,
              decoration: const InputDecoration(labelText: 'Descrição (opcional)'),
              maxLines: 2,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _cost,
              keyboardType: TextInputType.number,
              inputFormatters: [FilteringTextInputFormatter.digitsOnly],
              decoration: const InputDecoration(labelText: 'Custo em pontos', suffixText: 'pts'),
              onChanged: (_) => setState(() {}),
            ),
          ],
        ),
      ),
      actions: [
        if (!_isNew)
          TextButton(
            onPressed: () => Navigator.pop(context, const RewardDeleteRequested()),
            child: Text('Apagar', style: TextStyle(color: theme.colorScheme.error)),
          ),
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        ValueListenableBuilder(
          valueListenable: _title,
          builder: (context, value, _) => FilledButton(
            onPressed: value.text.trim().isEmpty || _costValue == null ? null : _save,
            child: const Text('Guardar'),
          ),
        ),
      ],
    );
  }
}
