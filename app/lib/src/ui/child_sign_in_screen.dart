import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../session/child_device.dart';
import '../session/session_controller.dart';
import 'design/components.dart';
import 'design/pin_pad.dart';
import 'scanner_support.dart';
import 'theme.dart';

/// Entrada de uma criança sem email: o código da família (que um adulto vê no ecrã dos membros), o seu nome e o PIN.
/// Depois da primeira entrada o aparelho lembra o código e abre logo em "Quem és tu?" (tablet partilhado, UX-11).
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
  void initState() {
    super.initState();
    final saved = ChildDevice.familyCode.value;
    if (saved != null) {
      _code.text = saved;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _findProfiles();
      });
    }
  }

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
      showError(context, 'Esta casa ainda não tem perfis de criança. Pede a um adulto para criar o teu.'.tr);
      return;
    }
    setState(() => _profiles = profiles);
  });

  /// Lê o QR do código da família (no ecrã dos membros, no telemóvel de um adulto).
  Future<void> _scan() async {
    final code = await scanCode(
      context,
      title: 'Ler o código da família'.tr,
      hint: 'Aponta para o QR do código da família, no telemóvel de um adulto da casa.'.tr,
      accept: familyCodeFrom,
    );
    if (code == null || !mounted) return;
    _code.text = code;
    await _findProfiles();
  }

  Future<void> _signIn() => _run(() async {
    final navigator = Navigator.of(context);
    try {
      await context.read<SessionController>().signInChild(_code.text, _chosen!.userId, _pin.text);
      await ChildDevice.remember(_code.text.trim());
    } catch (_) {
      _pin.clear();
      rethrow;
    }
    // Com a sessão iniciada, o ecrã principal já está por baixo.
    navigator.popUntil((route) => route.isFirst);
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final profiles = _profiles;
    final chosen = _chosen;
    return Scaffold(
      appBar: AppBar(title: Text('Entrar como criança'.tr)),
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 400),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (profiles == null) ...[
                  Text(
                    'Qual é o código da família?'.tr,
                    style: theme.textTheme.titleLarge,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    'Pede-o a um adulto da casa: está no ecrã dos membros.'.tr,
                    style: theme.textTheme.bodyMedium,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 24),
                  TextField(
                    controller: _code,
                    autofocus: true,
                    decoration: InputDecoration(
                      labelText: 'Código da família'.tr,
                      suffixIcon: scannerAvailable
                          ? IconButton(
                              tooltip: 'Ler o QR do código da família'.tr,
                              icon: const Icon(Icons.qr_code_scanner_rounded),
                              onPressed: _busy ? null : _scan,
                            )
                          : null,
                    ),
                    textCapitalization: TextCapitalization.characters,
                    onSubmitted: (_) => _findProfiles(),
                  ),
                  const SizedBox(height: 16),
                  FilledButton(onPressed: _busy ? null : _findProfiles, child: Text('Continuar'.tr)),
                ] else if (chosen == null) ...[
                  Text('Quem és tu?'.tr, style: theme.textTheme.headlineSmall, textAlign: TextAlign.center),
                  const SizedBox(height: 24),
                  // Avatares grandes em grelha: uma criança pequena encontra-se pela cor e pela letra.
                  Wrap(
                    alignment: WrapAlignment.center,
                    spacing: 16,
                    runSpacing: 16,
                    children: [
                      for (final p in profiles)
                        InkWell(
                          borderRadius: BorderRadius.circular(KubicoRadius.lg),
                          onTap: () => setState(() => _chosen = p),
                          child: Padding(
                            padding: const EdgeInsets.all(8),
                            child: SizedBox(
                              width: 104,
                              child: Column(
                                children: [
                                  MemberAvatar(name: p.displayName, color: p.color, emoji: p.avatar, size: 88),
                                  const SizedBox(height: 8),
                                  Text(
                                    p.displayName,
                                    style: theme.textTheme.titleMedium,
                                    textAlign: TextAlign.center,
                                    maxLines: 2,
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                ],
                              ),
                            ),
                          ),
                        ),
                    ],
                  ),
                  const SizedBox(height: 16),
                  TextButton(
                    onPressed: () {
                      ChildDevice.forget();
                      _code.clear();
                      setState(() => _profiles = null);
                    },
                    child: Text('Mudar o código'.tr),
                  ),
                ] else ...[
                  MemberAvatar(name: chosen.displayName, color: chosen.color, size: 88),
                  const SizedBox(height: 12),
                  Text(
                    'Olá, ${chosen.displayName}!'.tr,
                    style: theme.textTheme.headlineSmall,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'Escreve o teu PIN.'.tr,
                    style: theme.textTheme.bodyLarge?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 24),
                  PinPad(controller: _pin, onSubmit: _busy ? () {} : _signIn),
                  const SizedBox(height: 8),
                  TextButton(
                    onPressed: () => setState(() {
                      _chosen = null;
                      _pin.clear();
                    }),
                    child: Text('Não sou eu'.tr),
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

/// O código da família num QR lido (só letras e algarismos), ou null se não parecer um.
String? familyCodeFrom(String raw) {
  final code = raw.trim().toUpperCase();
  return RegExp(r'^[A-Z0-9]{4,16}$').hasMatch(code) ? code : null;
}
