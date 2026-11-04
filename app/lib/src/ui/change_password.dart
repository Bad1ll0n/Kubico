import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../l10n/strings.dart';
import 'theme.dart';

/// "Mudar a palavra-passe" na conta: pede a atual e a nova (duas vezes). As outras sessões terminam.
class ChangePasswordCard extends StatelessWidget {
  const ChangePasswordCard({super.key});

  @override
  Widget build(BuildContext context) => Card(
    child: ListTile(
      leading: const Icon(Icons.password_rounded),
      title: Text('Mudar a palavra-passe'.tr),
      subtitle: Text('Termina a sessão nos outros dispositivos.'.tr),
      trailing: const Icon(Icons.chevron_right_rounded),
      onTap: () async {
        final changed = await showDialog<bool>(context: context, builder: (_) => const _ChangePasswordDialog());
        if (changed == true && context.mounted) {
          ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Palavra-passe mudada.'.tr)));
        }
      },
    ),
  );
}

class _ChangePasswordDialog extends StatefulWidget {
  const _ChangePasswordDialog();

  @override
  State<_ChangePasswordDialog> createState() => _ChangePasswordDialogState();
}

class _ChangePasswordDialogState extends State<_ChangePasswordDialog> {
  final _form = GlobalKey<FormState>();
  final _current = TextEditingController();
  final _new = TextEditingController();
  final _repeat = TextEditingController();
  var _busy = false;
  String? _error;

  @override
  void dispose() {
    _current.dispose();
    _new.dispose();
    _repeat.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (!_form.currentState!.validate()) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await context.read<ApiClient>().changePassword(_current.text, _new.text);
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text('Mudar a palavra-passe'.tr),
    content: Form(
      key: _form,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          TextFormField(
            controller: _current,
            obscureText: true,
            autofocus: true,
            decoration: InputDecoration(labelText: 'Palavra-passe atual'.tr),
            validator: (v) => (v ?? '').isEmpty ? 'Escreve a palavra-passe atual.'.tr : null,
          ),
          const SizedBox(height: 12),
          TextFormField(
            controller: _new,
            obscureText: true,
            decoration: InputDecoration(labelText: 'Nova palavra-passe'.tr),
            validator: (v) => (v ?? '').length < 8 ? 'A palavra-passe tem de ter pelo menos 8 caracteres.'.tr : null,
          ),
          const SizedBox(height: 12),
          TextFormField(
            controller: _repeat,
            obscureText: true,
            decoration: InputDecoration(labelText: 'Repete a nova palavra-passe'.tr),
            validator: (v) => v != _new.text ? 'As palavras-passe não são iguais.'.tr : null,
            onFieldSubmitted: (_) => _save(),
          ),
          if (_error != null) ...[
            const SizedBox(height: 12),
            Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
        ],
      ),
    ),
    actions: [
      TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Cancelar'.tr)),
      FilledButton(onPressed: _busy ? null : _save, child: Text('Mudar'.tr)),
    ],
  );
}
