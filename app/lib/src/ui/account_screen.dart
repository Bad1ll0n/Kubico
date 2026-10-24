import 'package:file_saver/file_saver.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
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
            SnackBar(
              content: Text(kIsWeb ? 'Os teus dados foram descarregados.'.tr : 'Os teus dados foram guardados.'.tr),
            ),
          );
      }
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _delete() async {
    final api = context.read<ApiClient>();
    List<AccountDeletionHousehold> plan;
    try {
      plan = await api.getAccountDeletion();
    } catch (e) {
      if (mounted) showError(context, e);
      return;
    }
    if (!mounted) return;
    final password = await showDialog<String>(
      context: context,
      builder: (_) => _DeleteAccountDialog(plan: plan),
    );
    if (password == null || !mounted) return;

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
      ..showSnackBar(SnackBar(content: Text('A tua conta foi apagada.'.tr)));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final user = context.watch<SessionController>().user;
    final child = user?.isChildProfile ?? false;
    return Scaffold(
      appBar: AppBar(title: Text('A minha conta'.tr)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
        children: [
          Card(
            child: ListTile(
              leading: const Icon(Icons.person_outline),
              title: Text(user?.displayName ?? ''),
              subtitle: Text(user?.email ?? 'Perfil de criança (entra com o código da família e o PIN)'.tr),
            ),
          ),
          const SizedBox(height: 12),
          const LanguageCard(),
          if (!child) ...[const SizedBox(height: 12), const TwoFactorSection()],
          const SizedBox(height: 12),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Os teus dados'.tr, style: theme.textTheme.titleMedium),
                  const SizedBox(height: 4),
                  Text(
                    'Um ficheiro com tudo o que o Kubico guarda sobre ti: a conta, as casas, as sessões, os avisos e o '
                            'que fizeste em cada casa (tarefas, compras, eventos, pedidos de recompensa, mesada).'
                        .tr,
                  ),
                  const SizedBox(height: 12),
                  FilledButton.tonalIcon(
                    onPressed: _busy ? null : _export,
                    icon: const Icon(Icons.download_outlined),
                    label: Text('Descarregar os meus dados'.tr),
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
                  Text('Apagar a conta'.tr, style: theme.textTheme.titleMedium),
                  const SizedBox(height: 4),
                  if (child)
                    Text('O teu perfil é gerido pelos adultos da casa: pede-lhes para o apagar.'.tr)
                  else ...[
                    Text('Sais de todas as casas e a tua conta deixa de existir. Não se pode desfazer.'.tr),
                    const SizedBox(height: 12),
                    OutlinedButton.icon(
                      onPressed: _busy ? null : _delete,
                      style: OutlinedButton.styleFrom(foregroundColor: theme.colorScheme.error),
                      icon: const Icon(Icons.delete_forever_outlined),
                      label: Text('Apagar a minha conta'.tr),
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

/// Explica o que acontece (a cada casa) e pede a palavra-passe outra vez.
class _DeleteAccountDialog extends StatefulWidget {
  const _DeleteAccountDialog({required this.plan});

  final List<AccountDeletionHousehold> plan;

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
    final blocked = widget.plan.where((h) => h.outcome == AccountDeletionOutcome.blocked).toList();
    return AlertDialog(
      title: Text('Apagar a tua conta?'.tr),
      content: SizedBox(
        width: 420,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                '• Sais de todas as casas. As casas onde mais ninguém tem conta são apagadas, com tudo o que têm '
                        '(incluindo os perfis de criança).\n'
                        '• Onde és a única pessoa a administrar, a administração passa para o adulto que lá está há mais tempo.\n'
                        '• O que fizeste nas casas (tarefas feitas, artigos, eventos) fica para os outros, sem o teu nome.\n'
                        '• Não se pode desfazer. Se quiseres, descarrega antes os teus dados.'
                    .tr,
              ),
              if (widget.plan.isNotEmpty) ...[
                const SizedBox(height: 12),
                for (final h in widget.plan)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: Text(switch (h.outcome) {
                      AccountDeletionOutcome.deleted => '«${h.householdName}» é apagada.'.tr,
                      AccountDeletionOutcome.adminPassesTo =>
                        '«${h.householdName}»: a administração passa para ${h.successorName}.'.tr,
                      AccountDeletionOutcome.blocked =>
                        '«${h.householdName}»: és o único administrador e não há outro adulto. Antes de apagares a conta, passa a administração a um adulto (ou muda o papel de alguém para Adulto).'
                            .tr,
                      AccountDeletionOutcome.leaves => '«${h.householdName}»: sais da casa.'.tr,
                    }, style: h.outcome == AccountDeletionOutcome.blocked ? TextStyle(color: error) : null),
                  ),
              ],
              const SizedBox(height: 16),
              TextField(
                controller: _password,
                autofocus: true,
                obscureText: true,
                decoration: InputDecoration(labelText: 'Palavra-passe'.tr, helperText: 'Para confirmar que és tu.'.tr),
                enabled: blocked.isEmpty,
                onChanged: (_) => setState(() {}),
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: error),
          onPressed: _password.text.isEmpty || blocked.isNotEmpty ? null : () => Navigator.pop(context, _password.text),
          child: Text('Apagar a conta'.tr),
        ),
      ],
    );
  }
}

/// A língua da app e dos avisos. Ao mudar, a app desenha-se outra vez já na língua nova.
class LanguageCard extends StatelessWidget {
  const LanguageCard({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Idioma'.tr, style: theme.textTheme.titleMedium),
            const SizedBox(height: 4),
            Text('O idioma da app e dos avisos que recebes.'.tr, style: theme.textTheme.bodyMedium),
            const SizedBox(height: 12),
            SegmentedButton<String>(
              segments: [
                for (final code in AppLanguage.all) ButtonSegment(value: code, label: Text(AppLanguage.names[code]!)),
              ],
              selected: {AppLanguage.code},
              onSelectionChanged: (selected) => context.read<SessionController>().setLanguage(selected.single),
            ),
          ],
        ),
      ),
    );
  }
}
