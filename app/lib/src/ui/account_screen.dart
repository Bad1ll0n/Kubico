import 'package:file_saver/file_saver.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../session/session_controller.dart';
import 'theme.dart';
import 'two_factor_section.dart';

/// Guarda o ficheiro com os dados: na web descarrega-o; nos telemóveis e computadores pergunta onde guardar.
/// Devolve null se a pessoa desistir.
Future<String?> saveExportFile(String name, Uint8List bytes) {
  final base = name.endsWith('.json') ? name.substring(0, name.length - 5) : name;
  return kIsWeb
      ? FileSaver.instance.saveFile(name: base, bytes: bytes, fileExtension: 'json', mimeType: MimeType.json)
      : FileSaver.instance.saveAs(name: base, bytes: bytes, fileExtension: 'json', mimeType: MimeType.json);
}

/// A minha conta: descarregar tudo o que o Kubico guarda sobre mim, e apagar a conta.
class AccountScreen extends StatefulWidget {
  const AccountScreen({super.key, this.saveFile = saveExportFile, this.onDeleted});

  final Future<String?> Function(String name, Uint8List bytes) saveFile;

  /// Arruma o que a app tem guardado (listas sem rede, tempo real, avisos) quando a conta é apagada.
  final Future<void> Function()? onDeleted;

  @override
  State<AccountScreen> createState() => _AccountScreenState();
}

class _AccountScreenState extends State<AccountScreen> {
  var _busy = false;

  Future<void> _export() async {
    setState(() => _busy = true);
    try {
      final (name, bytes) = await context.read<ApiClient>().exportAccount();
      final saved = await widget.saveFile(name, bytes);
      if (saved != null && mounted) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(
            SnackBar(content: Text(kIsWeb ? 'Os teus dados foram descarregados.' : 'Os teus dados foram guardados.')),
          );
      }
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _delete() async {
    final password = await showDialog<String>(context: context, builder: (_) => const _DeleteAccountDialog());
    if (password == null || !mounted) return;

    final api = context.read<ApiClient>();
    final session = context.read<SessionController>();
    final navigator = Navigator.of(context);
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      await api.deleteAccount(password);
    } catch (e) {
      if (mounted) {
        setState(() => _busy = false);
        showError(context, e);
      }
      return;
    }

    await widget.onDeleted?.call();
    navigator.popUntil((route) => route.isFirst);
    await session.forgetDeletedAccount();
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('A tua conta foi apagada.')));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final user = context.watch<SessionController>().user;
    final child = user?.isChildProfile ?? false;
    return Scaffold(
      appBar: AppBar(title: const Text('A minha conta')),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
        children: [
          Card(
            child: ListTile(
              leading: const Icon(Icons.person_outline),
              title: Text(user?.displayName ?? ''),
              subtitle: Text(user?.email ?? 'Perfil de criança (entra com o código da família e o PIN)'),
            ),
          ),
          if (!child) ...[const SizedBox(height: 12), const TwoFactorSection()],
          const SizedBox(height: 12),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Os teus dados', style: theme.textTheme.titleMedium),
                  const SizedBox(height: 4),
                  const Text(
                    'Um ficheiro com tudo o que o Kubico guarda sobre ti: a conta, as casas, as sessões, os avisos e o '
                    'que fizeste em cada casa (tarefas, compras, eventos, pedidos de recompensa, mesada).',
                  ),
                  const SizedBox(height: 12),
                  FilledButton.tonalIcon(
                    onPressed: _busy ? null : _export,
                    icon: const Icon(Icons.download_outlined),
                    label: const Text('Descarregar os meus dados'),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 12),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Apagar a conta', style: theme.textTheme.titleMedium),
                  const SizedBox(height: 4),
                  if (child)
                    const Text('O teu perfil é gerido pelos adultos da casa: pede-lhes para o apagar.')
                  else ...[
                    const Text('Sais de todas as casas e a tua conta deixa de existir. Não se pode desfazer.'),
                    const SizedBox(height: 12),
                    OutlinedButton.icon(
                      onPressed: _busy ? null : _delete,
                      style: OutlinedButton.styleFrom(foregroundColor: theme.colorScheme.error),
                      icon: const Icon(Icons.delete_forever_outlined),
                      label: const Text('Apagar a minha conta'),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Explica o que acontece e pede a palavra-passe outra vez.
class _DeleteAccountDialog extends StatefulWidget {
  const _DeleteAccountDialog();

  @override
  State<_DeleteAccountDialog> createState() => _DeleteAccountDialogState();
}

class _DeleteAccountDialogState extends State<_DeleteAccountDialog> {
  final _password = TextEditingController();

  @override
  void dispose() {
    _password.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final error = Theme.of(context).colorScheme.error;
    return AlertDialog(
      title: const Text('Apagar a tua conta?'),
      content: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              '• Sais de todas as casas. As casas onde mais ninguém tem conta são apagadas, com tudo o que têm '
              '(incluindo os perfis de criança).\n'
              '• Onde és a única pessoa a administrar, a administração passa para outra pessoa da casa.\n'
              '• O que fizeste nas casas (tarefas feitas, artigos, eventos) fica para os outros, sem o teu nome.\n'
              '• Não se pode desfazer. Se quiseres, descarrega antes os teus dados.',
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _password,
              autofocus: true,
              obscureText: true,
              decoration: const InputDecoration(labelText: 'Palavra-passe', helperText: 'Para confirmar que és tu.'),
              onChanged: (_) => setState(() {}),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: error),
          onPressed: _password.text.isEmpty ? null : () => Navigator.pop(context, _password.text),
          child: const Text('Apagar a conta'),
        ),
      ],
    );
  }
}
