import 'package:flutter/material.dart';

import '../l10n/strings.dart';

/// Pede um nome novo (o atual vem preenchido). Devolve o nome sem espaços à volta, ou null se se cancelar.
Future<String?> askName(BuildContext context, {required String title, required String current}) => showDialog<String>(
  context: context,
  builder: (_) => _RenameDialog(title: title, current: current),
);

class _RenameDialog extends StatefulWidget {
  const _RenameDialog({required this.title, required this.current});

  final String title;
  final String current;

  @override
  State<_RenameDialog> createState() => _RenameDialogState();
}

class _RenameDialogState extends State<_RenameDialog> {
  late final _name = TextEditingController(text: widget.current);
  final _form = GlobalKey<FormState>();

  @override
  void dispose() {
    _name.dispose();
    super.dispose();
  }

  void _save() {
    if (_form.currentState!.validate()) Navigator.pop(context, _name.text.trim());
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(widget.title),
    content: Form(
      key: _form,
      child: TextFormField(
        controller: _name,
        autofocus: true,
        maxLength: 100,
        textCapitalization: TextCapitalization.words,
        decoration: InputDecoration(labelText: 'Nome'.tr),
        validator: (v) => (v ?? '').trim().isEmpty ? 'Escreve um nome.'.tr : null,
        onFieldSubmitted: (_) => _save(),
      ),
    ),
    actions: [
      TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
      FilledButton(onPressed: _save, child: Text('Guardar'.tr)),
    ],
  );
}
