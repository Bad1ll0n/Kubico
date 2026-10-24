import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../households/invite_link.dart';
import '../l10n/strings.dart';
import '../session/session_controller.dart';
import 'scanner_screen.dart';
import 'theme.dart';

/// Criar uma casa nova ou entrar numa existente com um código de convite.
/// Aparece no primeiro uso e também quando se escolhe "Adicionar casa".
class OnboardingScreen extends StatefulWidget {
  const OnboardingScreen({super.key, this.canGoBack = false});

  final bool canGoBack;

  @override
  State<OnboardingScreen> createState() => _OnboardingScreenState();
}

class _OnboardingScreenState extends State<OnboardingScreen> {
  final _householdName = TextEditingController();
  final _inviteCode = TextEditingController();
  bool _busy = false;

  /// Os modelos para começar a casa (vêm do servidor; sem rede, cria-se vazia).
  List<HouseholdTemplate> _templates = const [];
  String _template = 'Blank';

  @override
  void initState() {
    super.initState();
    // Chegou-se aqui por um link de convite: o código já vem preenchido.
    _inviteCode.text = context.read<SessionController>().pendingInvite ?? '';
    _loadTemplates();
  }

  Future<void> _loadTemplates() async {
    try {
      final templates = await context.read<ApiClient>().getHouseholdTemplates();
      if (mounted) setState(() => _templates = templates);
    } catch (_) {
      // Sem modelos, a casa nasce vazia.
    }
  }

  Future<void> _scan() async {
    final code = await Navigator.of(context).push<String>(
      MaterialPageRoute(
        builder: (_) => ScannerScreen<String>(
          title: 'Ler o convite'.tr,
          hint: 'Aponta para o QR do convite, no telemóvel de quem já está na casa.'.tr,
          accept: inviteCodeFrom,
        ),
      ),
    );
    if (code != null) _inviteCode.text = code;
  }

  @override
  void dispose() {
    _householdName.dispose();
    _inviteCode.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function(SessionController session) action) async {
    setState(() => _busy = true);
    try {
      await action(context.read<SessionController>());
      if (mounted && widget.canGoBack) Navigator.of(context).pop();
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final session = context.watch<SessionController>();

    return Scaffold(
      appBar: AppBar(
        title: Text(widget.canGoBack ? 'Adicionar casa'.tr : 'Olá, ${session.user?.displayName ?? ''}'.tr),
        actions: [
          if (!widget.canGoBack)
            IconButton(tooltip: 'Sair'.tr, icon: const Icon(Icons.logout), onPressed: session.signOut),
        ],
      ),
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 440),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text('Criar uma casa'.tr, style: theme.textTheme.titleMedium),
                        const SizedBox(height: 4),
                        Text(
                          'Ficas a administrar a casa e podes convidar a família. Escolhe um modelo para começar já '
                                  'com listas e tarefas (mudas tudo depois).'
                              .tr,
                          style: theme.textTheme.bodySmall,
                        ),
                        const SizedBox(height: 16),
                        TextField(
                          controller: _householdName,
                          decoration: InputDecoration(labelText: 'Nome'.tr, hintText: 'Ex.: Casa da Amora'.tr),
                          textCapitalization: TextCapitalization.sentences,
                        ),
                        if (_templates.isNotEmpty) ...[
                          const SizedBox(height: 16),
                          Text('Começar com'.tr, style: theme.textTheme.titleSmall),
                          const SizedBox(height: 8),
                          for (final template in _templates)
                            _TemplateTile(
                              template: template,
                              selected: template.key == _template,
                              onTap: () => setState(() => _template = template.key),
                            ),
                        ],
                        const SizedBox(height: 12),
                        FilledButton(
                          onPressed: _busy
                              ? null
                              : () {
                                  if (_householdName.text.trim().isEmpty) return;
                                  _run(
                                    (s) => s.createHousehold(
                                      _householdName.text,
                                      template: _template == 'Blank' ? null : _template,
                                    ),
                                  );
                                },
                          child: Text('Criar casa'.tr),
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 16),
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text('Entrar com um convite'.tr, style: theme.textTheme.titleMedium),
                        const SizedBox(height: 4),
                        Text('Pede o código (ou o QR) a quem já está na casa.'.tr, style: theme.textTheme.bodySmall),
                        const SizedBox(height: 16),
                        TextField(
                          controller: _inviteCode,
                          decoration: InputDecoration(
                            labelText: 'Código'.tr,
                            hintText: 'Ex.: K7M2QX9P'.tr,
                            suffixIcon: scannerAvailable
                                ? IconButton(
                                    tooltip: 'Ler o QR do convite'.tr,
                                    icon: const Icon(Icons.qr_code_scanner),
                                    onPressed: _busy ? null : _scan,
                                  )
                                : null,
                          ),
                          textCapitalization: TextCapitalization.characters,
                        ),
                        const SizedBox(height: 12),
                        FilledButton.tonal(
                          onPressed: _busy
                              ? null
                              : () {
                                  if (_inviteCode.text.trim().isEmpty) return;
                                  _run((s) => s.joinHousehold(_inviteCode.text));
                                },
                          child: Text('Entrar na casa'.tr),
                        ),
                      ],
                    ),
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

/// Um modelo para escolher: o nome, para quem é e o que traz.
class _TemplateTile extends StatelessWidget {
  const _TemplateTile({required this.template, required this.selected, required this.onTap});

  final HouseholdTemplate template;
  final bool selected;
  final VoidCallback onTap;

  static IconData _icon(String key) => switch (key) {
    'FamilyWithKids' => Icons.family_restroom,
    'Couple' => Icons.favorite_border,
    'SharedHouse' => Icons.groups_outlined,
    'CaringForParents' => Icons.elderly,
    _ => Icons.add_home_outlined,
  };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final contents = template.contents;
    return Card.outlined(
      margin: const EdgeInsets.only(bottom: 8),
      color: selected ? theme.colorScheme.secondaryContainer : null,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: selected ? theme.colorScheme.primary : theme.colorScheme.outlineVariant),
      ),
      child: ListTile(
        onTap: onTap,
        selected: selected,
        leading: Icon(_icon(template.key)),
        title: Text(template.name),
        subtitle: Text(contents.isEmpty ? template.description : '${template.description}\n$contents'),
        isThreeLine: contents.isNotEmpty,
        trailing: selected ? const Icon(Icons.check_circle) : null,
      ),
    );
  }
}
