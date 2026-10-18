import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../session/session_controller.dart';
import 'theme.dart';

/// Entrada de uma criança sem email: o código da família (que um adulto vê no ecrã dos membros), o seu nome e o PIN.
class ChildSignInScreen extends StatefulWidget {
  const ChildSignInScreen({super.key});

  @override
  State<ChildSignInScreen> createState() => _ChildSignInScreenState();
}

class _ChildSignInScreenState extends State<ChildSignInScreen> {
  final _code = TextEditingController();
  final _pin = TextEditingController();
  List<ChildProfileOption>? _profiles;
  ChildProfileOption? _chosen;
  var _busy = false;

  @override
  void dispose() {
    _code.dispose();
    _pin.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() => _busy = true);
    try {
      await action();
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _findProfiles() => _run(() async {
    final profiles = await context.read<ApiClient>().childProfiles(_code.text.trim());
    if (!mounted) return;
    if (profiles.isEmpty) {
      showError(context, 'Esta casa ainda não tem perfis de criança. Pede a um adulto para criar o teu.');
      return;
    }
    setState(() => _profiles = profiles);
  });

  Future<void> _signIn() => _run(() async {
    final navigator = Navigator.of(context);
    await context.read<SessionController>().signInChild(_code.text, _chosen!.userId, _pin.text);
    // Com a sessão iniciada, o ecrã principal já está por baixo.
    navigator.popUntil((route) => route.isFirst);
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final profiles = _profiles;
    final chosen = _chosen;
    return Scaffold(
      appBar: AppBar(title: const Text('Entrar como criança')),
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 400),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (profiles == null) ...[
                  Text('Qual é o código da família?', style: theme.textTheme.titleLarge, textAlign: TextAlign.center),
                  const SizedBox(height: 8),
                  Text(
                    'Pede-o a um adulto da casa: está no ecrã dos membros.',
                    style: theme.textTheme.bodyMedium,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 24),
                  TextField(
                    controller: _code,
                    autofocus: true,
                    decoration: const InputDecoration(labelText: 'Código da família'),
                    textCapitalization: TextCapitalization.characters,
                    onSubmitted: (_) => _findProfiles(),
                  ),
                  const SizedBox(height: 16),
                  FilledButton(onPressed: _busy ? null : _findProfiles, child: const Text('Continuar')),
                ] else if (chosen == null) ...[
                  Text('Quem és tu?', style: theme.textTheme.titleLarge, textAlign: TextAlign.center),
                  const SizedBox(height: 16),
                  for (final p in profiles)
                    Card(
                      child: ListTile(
                        leading: CircleAvatar(
                          backgroundColor: colorFromHex(p.color),
                          foregroundColor: Colors.white,
                          child: Text(p.displayName.isEmpty ? '?' : p.displayName[0].toUpperCase()),
                        ),
                        title: Text(p.displayName, style: theme.textTheme.titleMedium),
                        onTap: () => setState(() => _chosen = p),
                      ),
                    ),
                  TextButton(onPressed: () => setState(() => _profiles = null), child: const Text('Mudar o código')),
                ] else ...[
                  CircleAvatar(
                    radius: 32,
                    backgroundColor: colorFromHex(chosen.color),
                    foregroundColor: Colors.white,
                    child: Text(chosen.displayName.isEmpty ? '?' : chosen.displayName[0].toUpperCase()),
                  ),
                  const SizedBox(height: 12),
                  Text('Olá, ${chosen.displayName}!', style: theme.textTheme.titleLarge, textAlign: TextAlign.center),
                  const SizedBox(height: 24),
                  TextField(
                    controller: _pin,
                    autofocus: true,
                    decoration: const InputDecoration(labelText: 'PIN'),
                    keyboardType: TextInputType.number,
                    obscureText: true,
                    maxLength: 6,
                    inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                    onSubmitted: (_) => _signIn(),
                  ),
                  const SizedBox(height: 8),
                  FilledButton(onPressed: _busy ? null : _signIn, child: const Text('Entrar')),
                  TextButton(
                    onPressed: () => setState(() {
                      _chosen = null;
                      _pin.clear();
                    }),
                    child: const Text('Não sou eu'),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}
