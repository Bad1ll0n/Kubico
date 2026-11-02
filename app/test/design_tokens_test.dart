import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/ui/design/components.dart';
import 'package:kubico/src/ui/theme.dart';

/// O contraste das cores do design system (WCAG 2.2): texto ≥ 4,5:1, gráficos e ícones ≥ 3:1. Uma cor nova que
/// falhe faz este teste falhar.
void main() {
  void expectContrast(Color text, Color background, double minimum, String what) {
    final ratio = contrastRatio(text, background);
    expect(ratio, greaterThanOrEqualTo(minimum), reason: '$what: ${ratio.toStringAsFixed(2)}:1');
  }

  for (final (name, scheme, colors) in [
    ('claro', KubicoPalette.light, KubicoColors.light),
    ('escuro', KubicoPalette.dark, KubicoColors.dark),
  ]) {
    test('tema $name: o texto lê-se sobre todas as superfícies', () {
      for (final surface in [
        scheme.surface,
        scheme.surfaceContainerLow,
        scheme.surfaceContainer,
        scheme.surfaceContainerHigh,
        scheme.surfaceContainerHighest,
      ]) {
        expectContrast(scheme.onSurface, surface, 4.5, 'onSurface');
        expectContrast(scheme.onSurfaceVariant, surface, 4.5, 'onSurfaceVariant');
        expectContrast(scheme.primary, surface, 3, 'primary (ícones)');
      }
      expectContrast(scheme.outline, scheme.surface, 4.5, 'outline sobre surface');
      expectContrast(scheme.outline, scheme.surfaceContainerLow, 4.5, 'outline sobre os cartões');
    });

    test('tema $name: cada cor com o seu par', () {
      expectContrast(scheme.onPrimary, scheme.primary, 4.5, 'primary');
      expectContrast(scheme.onPrimaryContainer, scheme.primaryContainer, 4.5, 'primaryContainer');
      expectContrast(scheme.onSecondary, scheme.secondary, 4.5, 'secondary');
      expectContrast(scheme.onSecondaryContainer, scheme.secondaryContainer, 4.5, 'secondaryContainer');
      expectContrast(scheme.onTertiary, scheme.tertiary, 4.5, 'tertiary');
      expectContrast(scheme.onTertiaryContainer, scheme.tertiaryContainer, 4.5, 'tertiaryContainer');
      expectContrast(scheme.onError, scheme.error, 4.5, 'error');
      expectContrast(scheme.onErrorContainer, scheme.errorContainer, 4.5, 'errorContainer');
      expectContrast(colors.onSuccessContainer, colors.successContainer, 4.5, 'successContainer');
      expectContrast(colors.onWarningContainer, colors.warningContainer, 4.5, 'warningContainer');
      expectContrast(colors.onInfoContainer, colors.infoContainer, 4.5, 'infoContainer');
      for (final c in [colors.success, colors.warning, colors.info]) {
        expectContrast(c, scheme.surface, 3, 'estado sobre surface');
      }
      for (final MapEntry(key: area, value: c) in colors.areas.entries) {
        expectContrast(c.onContainer, c.container, 4.5, 'área ${area.name}');
        expectContrast(c.color, scheme.surface, 3, 'ícone da área ${area.name}');
      }
    });

    test('tema $name: as cores dos membros (iniciais, blocos e gráficos)', () {
      final brightness = scheme.brightness;
      for (final color in MemberPalette.colors) {
        final tones = MemberPalette.tones(color.key, brightness);
        expectContrast(tones.onFill, tones.fill, 4.5, 'iniciais em ${color.key}');
        expectContrast(tones.onSoft, tones.soft, 4.5, 'texto do bloco em ${color.key}');
        expectContrast(tones.fill, scheme.surface, 3, '${color.key} como gráfico');
        expectContrast(tones.fill, scheme.surfaceContainerLow, 3, '${color.key} sobre os cartões');
      }
    });
  }

  test('os hex antigos dos membros passam para a paleta nova, e um hex desconhecido usa-se como vem', () {
    expect(MemberPalette.resolve('#F2CC8F').key, 'mostarda');
    expect(MemberPalette.resolve('#4f7cac').key, 'azul');
    expect(MemberPalette.resolve('#2F64A8').key, 'azul');
    final custom = MemberPalette.tones('#000000', Brightness.light);
    expect(custom.onFill, const Color(0xFFFFFFFF));
  });

  test('a inicial é o primeiro carácter visível', () {
    expect(initialOf('élia'), 'É');
    expect(initialOf('  rui'), 'R');
    expect(initialOf(''), '?');
    expect(initialOf('🦁 Leão'), '🦁');
  });
}
