import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../l10n/strings.dart';
import 'tokens.dart';

/// O PIN de uma criança: pontos que se enchem e um teclado de 3 × 4 com teclas grandes (72 dp), em vez do teclado
/// do sistema. Também aceita o teclado físico (algarismos, apagar e Enter). Escreve em [controller].
class PinPad extends StatefulWidget {
  const PinPad({required this.controller, required this.onSubmit, this.maxLength = 6, this.minLength = 4, super.key});

  final TextEditingController controller;
  final VoidCallback onSubmit;
  final int maxLength;
  final int minLength;

  @override
  State<PinPad> createState() => _PinPadState();
}

class _PinPadState extends State<PinPad> {
  final _focus = FocusNode();

  String get _pin => widget.controller.text;

  bool get _complete => _pin.length >= widget.minLength;

  @override
  void initState() {
    super.initState();
    widget.controller.addListener(_changed);
  }

  @override
  void dispose() {
    widget.controller.removeListener(_changed);
    _focus.dispose();
    super.dispose();
  }

  void _changed() => setState(() {});

  void _type(String digit) {
    if (_pin.length >= widget.maxLength) return;
    HapticFeedback.lightImpact();
    widget.controller.text = '$_pin$digit';
  }

  void _erase() {
    if (_pin.isEmpty) return;
    HapticFeedback.lightImpact();
    widget.controller.text = _pin.substring(0, _pin.length - 1);
  }

  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent) return KeyEventResult.ignored;
    final character = event.character;
    if (character != null && RegExp(r'^\d$').hasMatch(character)) {
      _type(character);
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.backspace) {
      _erase();
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.enter && _complete) {
      widget.onSubmit();
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  Widget _key(BuildContext context, String id, {VoidCallback? onTap, Widget? child, String? label}) {
    final scheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.all(6),
      child: Semantics(
        button: true,
        enabled: onTap != null,
        label: label ?? id,
        excludeSemantics: true,
        child: Material(
          color: onTap == null ? Colors.transparent : scheme.surfaceContainerHigh,
          shape: const CircleBorder(),
          child: InkWell(
            key: ValueKey('pin-key-$id'),
            customBorder: const CircleBorder(),
            onTap: onTap,
            child: SizedBox.square(
              dimension: 72,
              child: Center(
                child:
                    child ??
                    Text(
                      id,
                      textScaler: TextScaler.noScaling,
                      style: Theme.of(context).textTheme.headlineSmall?.copyWith(color: scheme.onSurface),
                    ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final dots = _pin.length > widget.minLength ? _pin.length : widget.minLength;
    return Focus(
      focusNode: _focus,
      autofocus: true,
      onKeyEvent: _onKey,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Semantics(
            label: '${_pin.length} algarismos escritos'.tr,
            excludeSemantics: true,
            child: Row(
              key: ValueKey('pin-dots-${_pin.length}'),
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                for (var i = 0; i < dots; i++)
                  Container(
                    margin: const EdgeInsets.symmetric(horizontal: 8),
                    width: 18,
                    height: 18,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: i < _pin.length ? scheme.primary : Colors.transparent,
                      border: Border.all(color: scheme.outline, width: 2),
                    ),
                  ),
              ],
            ),
          ),
          const SizedBox(height: KubicoSpace.s24),
          for (final row in const [
            ['1', '2', '3'],
            ['4', '5', '6'],
            ['7', '8', '9'],
          ])
            Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [for (final d in row) _key(context, d, onTap: () => _type(d))],
            ),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              _key(
                context,
                'erase',
                label: 'Apagar'.tr,
                onTap: _pin.isEmpty ? null : _erase,
                child: Icon(Icons.backspace_rounded, color: scheme.onSurfaceVariant),
              ),
              _key(context, '0', onTap: () => _type('0')),
              _key(
                context,
                'ok',
                label: 'Entrar'.tr,
                // Sempre ativa (um toque logo a seguir ao último algarismo não se perde); só envia com o PIN completo.
                onTap: () {
                  if (_complete) widget.onSubmit();
                },
                child: Icon(
                  Icons.arrow_forward_rounded,
                  size: 32,
                  color: _complete ? scheme.primary : scheme.outlineVariant,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
