import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../l10n/strings.dart';
import '../session/session_controller.dart';
import 'child_sign_in_screen.dart';
import 'theme.dart';

class AuthScreen extends StatefulWidget {
  const AuthScreen({super.key});

  @override
  State<AuthScreen> createState() => _AuthScreenState();
}

class _AuthScreenState extends State<AuthScreen> {
  final _formKey = GlobalKey<FormState>();
  final _name = TextEditingController();
  final _email = TextEditingController();
  final _password = TextEditingController();
  bool _isRegister = false;
  bool _busy = false;

  /// Depois da palavra-passe certa, numa conta com verificação em dois passos: falta o código.
  String? _challenge;
  final _code = TextEditingController();
  var _recovery = false;

  @override
  void dispose() {
    _name.dispose();
    _email.dispose();
    _password.dispose();
    _code.dispose();
    super.dispose();
  }

  Future<void> _submitCode() async {
    if (_code.text.trim().isEmpty) return;
    setState(() => _busy = true);
    try {
      await context.read<SessionController>().signInWithCode(_challenge!, _code.text);
    } on ApiException catch (e) {
      if (!mounted) return;
      // O bilhete expirou: volta-se à palavra-passe.
      if (e.statusCode == 401 &&
          (e.message.startsWith('Passou demasiado tempo') || e.message.startsWith('Too much time has passed'))) {
        _backToPassword();
      }
      showError(context, e);
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _backToPassword() => setState(() {
    _challenge = null;
    _recovery = false;
    _code.clear();
  });

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() => _busy = true);
    final session = context.read<SessionController>();
    try {
      if (_isRegister) {
        await session.register(_email.text, _password.text, _name.text);
      } else {
        await session.signIn(_email.text, _password.text);
      }
    } on TwoFactorRequired catch (e) {
      if (mounted) setState(() => _challenge = e.challengeToken);
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// O segundo passo: o código da app de autenticação, ou um de recuperação para quem perdeu o telemóvel.
  List<Widget> _codeStep(ThemeData theme) => [
    Text('Verificação em dois passos'.tr, textAlign: TextAlign.center, style: theme.textTheme.titleMedium),
    const SizedBox(height: 8),
    Text(
      _recovery
          ? 'Escreve um dos teus códigos de recuperação (ex.: ABCD-EFGH).'.tr
          : 'Escreve o código de 6 algarismos da tua app de autenticação.'.tr,
      textAlign: TextAlign.center,
    ),
    const SizedBox(height: 16),
    TextField(
      key: ValueKey(_recovery),
      controller: _code,
      autofocus: true,
      keyboardType: _recovery ? TextInputType.text : TextInputType.number,
      textCapitalization: TextCapitalization.characters,
      autofillHints: const [AutofillHints.oneTimeCode],
      decoration: InputDecoration(labelText: _recovery ? 'Código de recuperação'.tr : 'Código'.tr),
      onSubmitted: (_) => _submitCode(),
    ),
    const SizedBox(height: 24),
    FilledButton(
      onPressed: _busy ? null : _submitCode,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: _busy
            ? const SizedBox.square(dimension: 20, child: CircularProgressIndicator(strokeWidth: 2))
            : Text('Entrar'.tr),
      ),
    ),
    const SizedBox(height: 8),
    TextButton(
      onPressed: _busy
          ? null
          : () => setState(() {
              _recovery = !_recovery;
              _code.clear();
            }),
      child: Text(_recovery ? 'Usar o código da app'.tr : 'Perdi o telemóvel: usar um código de recuperação'.tr),
    ),
    TextButton(onPressed: _busy ? null : _backToPassword, child: Text('Voltar'.tr)),
  ];

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 400),
            child: Form(
              key: _formKey,
              child: AutofillGroup(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    // Antes de entrar já se escolhe a língua (fica guardada no dispositivo).
                    Align(
                      alignment: Alignment.centerRight,
                      child: TextButton.icon(
                        icon: const Icon(Icons.language),
                        label: Text(AppLanguage.isEnglish ? 'Português' : 'English'),
                        onPressed: () =>
                            AppLanguage.set(AppLanguage.isEnglish ? AppLanguage.portuguese : AppLanguage.english),
                      ),
                    ),
                    Icon(Icons.cottage_rounded, size: 56, color: theme.colorScheme.primary),
                    const SizedBox(height: 12),
                    Text('Kubico', textAlign: TextAlign.center, style: theme.textTheme.headlineMedium),
                    const SizedBox(height: 4),
                    Text(
                      'A casa toda, organizada em conjunto.'.tr,
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    ),
                    const SizedBox(height: 32),
                    if (_challenge != null)
                      ..._codeStep(theme)
                    else ...[
                      if (_isRegister) ...[
                        TextFormField(
                          controller: _name,
                          decoration: InputDecoration(labelText: 'Nome'.tr),
                          textCapitalization: TextCapitalization.words,
                          autofillHints: const [AutofillHints.name],
                          validator: (v) => (v == null || v.trim().isEmpty) ? 'Indica o teu nome.'.tr : null,
                        ),
                        const SizedBox(height: 16),
                      ],
                      TextFormField(
                        controller: _email,
                        decoration: const InputDecoration(labelText: 'Email'),
                        keyboardType: TextInputType.emailAddress,
                        autofillHints: const [AutofillHints.email],
                        validator: (v) => (v == null || !v.contains('@')) ? 'Indica um email válido.'.tr : null,
                      ),
                      const SizedBox(height: 16),
                      TextFormField(
                        controller: _password,
                        decoration: InputDecoration(labelText: 'Palavra-passe'.tr),
                        obscureText: true,
                        autofillHints: [_isRegister ? AutofillHints.newPassword : AutofillHints.password],
                        onFieldSubmitted: (_) => _submit(),
                        validator: (v) => (_isRegister && (v == null || v.length < 8))
                            ? 'Usa pelo menos 8 caracteres.'.tr
                            : (v == null || v.isEmpty ? 'Indica a palavra-passe.'.tr : null),
                      ),
                      const SizedBox(height: 24),
                      FilledButton(
                        onPressed: _busy ? null : _submit,
                        child: Padding(
                          padding: const EdgeInsets.symmetric(vertical: 12),
                          child: _busy
                              ? const SizedBox.square(dimension: 20, child: CircularProgressIndicator(strokeWidth: 2))
                              : Text(_isRegister ? 'Criar conta'.tr : 'Entrar'.tr),
                        ),
                      ),
                      const SizedBox(height: 8),
                      TextButton(
                        onPressed: _busy ? null : () => setState(() => _isRegister = !_isRegister),
                        child: Text(_isRegister ? 'Já tenho conta'.tr : 'Criar uma conta nova'.tr),
                      ),
                      if (!_isRegister)
                        TextButton.icon(
                          onPressed: _busy
                              ? null
                              : () =>
                                    Navigator.of(context)
                                        .push(MaterialPageRoute<void>(builder: (_) => const ChildSignInScreen())),
                          icon: const Icon(Icons.child_care),
                          label: Text('Entrar com perfil de criança'.tr),
                        ),
                    ],
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
