import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../api/api_client.dart';
import 'theme.dart';

/// Verificação em dois passos, no ecrã da conta: ligar (com o código QR da app de autenticação) e desligar.
class TwoFactorSection extends StatefulWidget {
  const TwoFactorSection({super.key});

  @override
  State<TwoFactorSection> createState() => _TwoFactorSectionState();
}

class _TwoFactorSectionState extends State<TwoFactorSection> {
  ({bool enabled, int recoveryCodesLeft})? _status;
  var _busy = false;

  ApiClient get _api => context.read<ApiClient>();

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final status = await _api.getTwoFactor();
      if (mounted) setState(() => _status = status);
    } catch (_) {
      // Fica sem estado; o resto do ecrã funciona.
    }
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() => _busy = true);
    try {
      await action();
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _turnOn() => _run(() async {
    final (secret, uri) = await _api.setUpTwoFactor();
    if (!mounted) return;
    final codes = await showDialog<List<String>>(
      context: context,
      barrierDismissible: false,
      builder: (_) => _SetupDialog(secret: secret, uri: uri),
    );
    if (codes == null || !mounted) return;
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) => _RecoveryCodesDialog(codes: codes),
    );
  });

  Future<void> _turnOff() async {
    final answer = await showDialog<({String password, String code})>(
      context: context,
      builder: (_) => const _TurnOffDialog(),
    );
    if (answer == null) return;
    await _run(() async {
      await _api.disableTwoFactor(answer.password, answer.code);
      if (mounted) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(const SnackBar(content: Text('Verificação em dois passos desligada.')));
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final status = _status;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(child: Text('Verificação em dois passos', style: theme.textTheme.titleMedium)),
                if (status?.enabled ?? false)
                  Chip(
                    avatar: const Icon(Icons.verified_user_outlined, size: 18),
                    label: const Text('Ligada'),
                    visualDensity: VisualDensity.compact,
                  ),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              status?.enabled ?? false
                  ? 'Para entrar, além da palavra-passe, é preciso o código da app de autenticação. '
                        'Tens ${status!.recoveryCodesLeft} códigos de recuperação por usar.'
                  : 'Além da palavra-passe, pede um código que muda a cada 30 segundos, de uma app de autenticação '
                        'no telemóvel (Google Authenticator, Microsoft Authenticator…). Assim, quem souber a tua '
                        'palavra-passe não entra sem o teu telemóvel.',
            ),
            const SizedBox(height: 12),
            if (status != null)
              status.enabled
                  ? OutlinedButton.icon(
                      onPressed: _busy ? null : _turnOff,
                      icon: const Icon(Icons.lock_open_outlined),
                      label: const Text('Desligar'),
                    )
                  : FilledButton.tonalIcon(
                      onPressed: _busy ? null : _turnOn,
                      icon: const Icon(Icons.verified_user_outlined),
                      label: const Text('Ligar'),
                    ),
          ],
        ),
      ),
    );
  }
}

/// Mostra o código QR (e o segredo, para escrever à mão) e pede o primeiro código da app para confirmar.
class _SetupDialog extends StatefulWidget {
  const _SetupDialog({required this.secret, required this.uri});

  final String secret;
  final String uri;

  @override
  State<_SetupDialog> createState() => _SetupDialogState();
}

class _SetupDialogState extends State<_SetupDialog> {
  final _code = TextEditingController();
  var _busy = false;
  String? _error;

  @override
  void dispose() {
    _code.dispose();
    super.dispose();
  }

  /// "ABCD EFGH IJKL …", mais fácil de copiar à mão.
  String get _grouped => RegExp('.{1,4}').allMatches(widget.secret).map((m) => m.group(0)).join(' ');

  Future<void> _confirm() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final codes = await context.read<ApiClient>().enableTwoFactor(_code.text.trim());
      if (mounted) Navigator.pop(context, codes);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: const Text('Ligar a verificação em dois passos'),
      content: SizedBox(
        width: 420,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Text('1. Na app de autenticação do telemóvel, adiciona uma conta e lê este código:'),
              const SizedBox(height: 12),
              Container(
                color: Colors.white,
                padding: const EdgeInsets.all(8),
                child: QrImageView(data: widget.uri, size: 180, backgroundColor: Colors.white),
              ),
              const SizedBox(height: 8),
              Text('Ou escreve esta chave:', style: theme.textTheme.bodySmall),
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Flexible(
                    child: SelectableText(
                      _grouped,
                      textAlign: TextAlign.center,
                      style: theme.textTheme.titleSmall?.copyWith(letterSpacing: 1.5, fontFamily: 'monospace'),
                    ),
                  ),
                  IconButton(
                    tooltip: 'Copiar a chave',
                    icon: const Icon(Icons.copy, size: 18),
                    onPressed: () => Clipboard.setData(ClipboardData(text: widget.secret)),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              const Align(
                alignment: Alignment.centerLeft,
                child: Text('2. Escreve o código de 6 algarismos que a app mostra:'),
              ),
              const SizedBox(height: 8),
              TextField(
                controller: _code,
                autofocus: true,
                keyboardType: TextInputType.number,
                autofillHints: const [AutofillHints.oneTimeCode],
                maxLength: 7,
                decoration: InputDecoration(labelText: 'Código', counterText: '', errorText: _error, errorMaxLines: 3),
                onChanged: (_) => setState(() {}),
                onSubmitted: (_) => _code.text.trim().length >= 6 ? _confirm() : null,
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: _busy ? null : () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(
          onPressed: _busy || _code.text.trim().length < 6 ? null : _confirm,
          child: const Text('Confirmar'),
        ),
      ],
    );
  }
}

/// Os códigos de recuperação, que só se mostram uma vez.
class _RecoveryCodesDialog extends StatelessWidget {
  const _RecoveryCodesDialog({required this.codes});

  final List<String> codes;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: const Text('Guarda os códigos de recuperação'),
      content: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Se perderes o telemóvel, entras com um destes códigos em vez do da app. Cada um serve uma vez. '
              'Guarda-os num sítio seguro: não voltam a aparecer.',
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 16,
              runSpacing: 8,
              children: [
                for (final code in codes)
                  Text(code, style: theme.textTheme.titleMedium?.copyWith(fontFamily: 'monospace', letterSpacing: 1)),
              ],
            ),
          ],
        ),
      ),
      actions: [
        TextButton.icon(
          onPressed: () {
            Clipboard.setData(ClipboardData(text: codes.join('\n')));
            ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Códigos copiados.')));
          },
          icon: const Icon(Icons.copy, size: 18),
          label: const Text('Copiar'),
        ),
        FilledButton(onPressed: () => Navigator.pop(context), child: const Text('Já guardei')),
      ],
    );
  }
}

/// Para desligar: a palavra-passe e um código da app (ou de recuperação).
class _TurnOffDialog extends StatefulWidget {
  const _TurnOffDialog();

  @override
  State<_TurnOffDialog> createState() => _TurnOffDialogState();
}

class _TurnOffDialogState extends State<_TurnOffDialog> {
  final _password = TextEditingController();
  final _code = TextEditingController();

  @override
  void dispose() {
    _password.dispose();
    _code.dispose();
    super.dispose();
  }

  bool get _ready => _password.text.isNotEmpty && _code.text.trim().length >= 6;

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: const Text('Desligar a verificação em dois passos?'),
    content: SizedBox(
      width: 400,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Text('Para entrar passa a bastar a palavra-passe.'),
          const SizedBox(height: 12),
          TextField(
            controller: _password,
            autofocus: true,
            obscureText: true,
            decoration: const InputDecoration(labelText: 'Palavra-passe'),
            onChanged: (_) => setState(() {}),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _code,
            decoration: const InputDecoration(labelText: 'Código da app ou de recuperação'),
            onChanged: (_) => setState(() {}),
          ),
        ],
      ),
    ),
    actions: [
      TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
      FilledButton(
        onPressed: _ready
            ? () => Navigator.pop<({String password, String code})>(context, (
                password: _password.text,
                code: _code.text.trim(),
              ))
            : null,
        child: const Text('Desligar'),
      ),
    ],
  );
}
