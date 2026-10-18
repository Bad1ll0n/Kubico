import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../households/invite_link.dart';
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

  @override
  void initState() {
    super.initState();
    // Chegou-se aqui por um link de convite: o código já vem preenchido.
    _inviteCode.text = context.read<SessionController>().pendingInvite ?? '';
  }

  Future<void> _scan() async {
    final code = await Navigator.of(context).push<String>(
      MaterialPageRoute(
        builder: (_) => const ScannerScreen<String>(
          title: 'Ler o convite',
          hint: 'Aponta para o QR do convite, no telemóvel de quem já está na casa.',
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
        title: Text(widget.canGoBack ? 'Adicionar casa' : 'Olá, ${session.user?.displayName ?? ''}'),
        actions: [
          if (!widget.canGoBack)
            IconButton(tooltip: 'Sair', icon: const Icon(Icons.logout), onPressed: session.signOut),
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
                        Text('Criar uma casa', style: theme.textTheme.titleMedium),
                        const SizedBox(height: 4),
                        Text(
                          'Ficas a administrar a casa e podes convidar a família.',
                          style: theme.textTheme.bodySmall,
                        ),
                        const SizedBox(height: 16),
                        TextField(
                          controller: _householdName,
                          decoration: const InputDecoration(labelText: 'Nome', hintText: 'Ex.: Casa da Amora'),
                          textCapitalization: TextCapitalization.sentences,
                        ),
                        const SizedBox(height: 12),
                        FilledButton(
                          onPressed: _busy
                              ? null
                              : () {
                                  if (_householdName.text.trim().isEmpty) return;
                                  _run((s) => s.createHousehold(_householdName.text));
                                },
                          child: const Text('Criar casa'),
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
                        Text('Entrar com um convite', style: theme.textTheme.titleMedium),
                        const SizedBox(height: 4),
                        Text('Pede o código (ou o QR) a quem já está na casa.', style: theme.textTheme.bodySmall),
                        const SizedBox(height: 16),
                        TextField(
                          controller: _inviteCode,
                          decoration: InputDecoration(
                            labelText: 'Código',
                            hintText: 'Ex.: K7M2QX9P',
                            suffixIcon: scannerAvailable
                                ? IconButton(
                                    tooltip: 'Ler o QR do convite',
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
                          child: const Text('Entrar na casa'),
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
