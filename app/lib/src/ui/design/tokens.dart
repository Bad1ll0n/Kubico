import 'package:flutter/material.dart';

/// O design system do Kubico: cores escritas à mão (não `ColorScheme.fromSeed`, que transformava a terracota da
/// marca num castanho e dava a mesma cor a todos os contentores), espaçamentos, raios e movimento. Todos os pares de
/// texto e fundo foram verificados (WCAG AA); o `design_tokens_test.dart` volta a verificá-los.
abstract final class KubicoPalette {
  // Claro: verde-salva e creme quente, sem branco puro (cansa menos os olhos); o damasco dá o calor.
  static const light = ColorScheme(
    brightness: Brightness.light,
    primary: Color(0xFF3E6B57),
    onPrimary: Color(0xFFFFFFFF),
    primaryContainer: Color(0xFFD5E8DD),
    onPrimaryContainer: Color(0xFF0F3325),
    secondary: Color(0xFF8C4A2F),
    onSecondary: Color(0xFFFFFFFF),
    secondaryContainer: Color(0xFFFFDCCB),
    onSecondaryContainer: Color(0xFF3A1406),
    tertiary: Color(0xFF7A5600),
    onTertiary: Color(0xFFFFFFFF),
    tertiaryContainer: Color(0xFFFBE3A6),
    onTertiaryContainer: Color(0xFF2E2000),
    error: Color(0xFFB3261E),
    onError: Color(0xFFFFFFFF),
    errorContainer: Color(0xFFF9DEDC),
    onErrorContainer: Color(0xFF410E0B),
    surface: Color(0xFFF7F5EF),
    onSurface: Color(0xFF1F2620),
    surfaceContainerLowest: Color(0xFFFCFBF7),
    surfaceContainerLow: Color(0xFFF2F0E9),
    surfaceContainer: Color(0xFFECEAE2),
    surfaceContainerHigh: Color(0xFFE6E4DC),
    surfaceContainerHighest: Color(0xFFDFDDD4),
    onSurfaceVariant: Color(0xFF4A524B),
    outline: Color(0xFF626962),
    outlineVariant: Color(0xFFC8CCC4),
    inverseSurface: Color(0xFF2E352F),
    onInverseSurface: Color(0xFFEEF1EB),
    inversePrimary: Color(0xFFA5D1BB),
    shadow: Color(0xFF000000),
    scrim: Color(0xFF000000),
    surfaceTint: Color(0xFF3E6B57),
  );

  // Escuro: carvão esverdeado, sem pretos puros nem brancos puros (o texto é um branco-cinza suave).
  static const dark = ColorScheme(
    brightness: Brightness.dark,
    primary: Color(0xFFA5D1BB),
    onPrimary: Color(0xFF0F3325),
    primaryContainer: Color(0xFF2B4F3F),
    onPrimaryContainer: Color(0xFFD5E8DD),
    secondary: Color(0xFFFFB696),
    onSecondary: Color(0xFF4F230F),
    secondaryContainer: Color(0xFF6E3520),
    onSecondaryContainer: Color(0xFFFFDCCB),
    tertiary: Color(0xFFE9C46A),
    onTertiary: Color(0xFF3A2A00),
    tertiaryContainer: Color(0xFF574000),
    onTertiaryContainer: Color(0xFFFBE3A6),
    error: Color(0xFFFFB4AB),
    onError: Color(0xFF690005),
    errorContainer: Color(0xFF8C1D18),
    onErrorContainer: Color(0xFFF9DEDC),
    surface: Color(0xFF161A17),
    onSurface: Color(0xFFE3E7E1),
    surfaceContainerLowest: Color(0xFF101411),
    surfaceContainerLow: Color(0xFF1D221E),
    surfaceContainer: Color(0xFF222723),
    surfaceContainerHigh: Color(0xFF2C312D),
    surfaceContainerHighest: Color(0xFF373C38),
    onSurfaceVariant: Color(0xFFC2C8C0),
    outline: Color(0xFF8C938B),
    outlineVariant: Color(0xFF424943),
    inverseSurface: Color(0xFFE3E7E1),
    onInverseSurface: Color(0xFF2E352F),
    inversePrimary: Color(0xFF3E6B57),
    shadow: Color(0xFF000000),
    scrim: Color(0xFF000000),
    surfaceTint: Color(0xFFA5D1BB),
  );
}

/// As áreas da app, cada uma com a sua cor (para crianças e avós, a cor diz "onde estou").
enum KubicoArea { today, shopping, chores, calendar, rewards }

/// A cor de uma área: o ícone ou indicador, o contentor e o texto sobre o contentor.
@immutable
class AreaColors {
  const AreaColors(this.color, this.container, this.onContainer);

  final Color color;
  final Color container;
  final Color onContainer;
}

/// Os tons de uma cor de membro já resolvidos para o tema: [fill] é o fundo do avatar (com as iniciais em
/// [onFill]), a barra do evento e os gráficos; [soft] é o fundo de um bloco ou chip, com o texto em [onSoft].
@immutable
class MemberTones {
  const MemberTones({required this.fill, required this.onFill, required this.soft, required this.onSoft});

  final Color fill;
  final Color onFill;
  final Color soft;
  final Color onSoft;
}

/// Uma cor de membro: o tom forte (claro), o claro (escuro) e os suaves de cada tema.
@immutable
class MemberColor {
  const MemberColor(this.key, this.strong, this.light, this.softLight, this.softDark);

  final String key;
  final Color strong;
  final Color light;
  final Color softLight;
  final Color softDark;
}

/// As 11 cores de membro, todas com ≥ 4,5:1 para as iniciais (brancas em claro, escuras em escuro). O servidor
/// guarda o hex do tom forte; os hex antigos (anteriores a esta paleta, que falhavam o contraste) continuam a
/// resolver-se para a cor nova mais parecida.
abstract final class MemberPalette {
  static const colors = [
    MemberColor('azul', Color(0xFF2F64A8), Color(0xFFA9C7FF), Color(0xFFDCE7FA), Color(0xFF1F3554)),
    MemberColor('laranja', Color(0xFFA9500B), Color(0xFFFFB68A), Color(0xFFFBE2D0), Color(0xFF55290A)),
    MemberColor('oliva', Color(0xFF4F6B1F), Color(0xFFBFD88A), Color(0xFFE6EDD0), Color(0xFF2A3810)),
    MemberColor('mostarda', Color(0xFF7A6200), Color(0xFFE3CB5C), Color(0xFFF4EBC4), Color(0xFF3D3200)),
    MemberColor('roxo', Color(0xFF7449B8), Color(0xFFCDB4FF), Color(0xFFEBDFFB), Color(0xFF3A2560)),
    MemberColor('verde', Color(0xFF2E7550), Color(0xFF8FD6AE), Color(0xFFD5EFDF), Color(0xFF173D2A)),
    MemberColor('rosa', Color(0xFFB23A73), Color(0xFFFFB0D2), Color(0xFFFADDE9), Color(0xFF5A1D3A)),
    MemberColor('petroleo', Color(0xFF12737A), Color(0xFF7FD8DE), Color(0xFFD2EEF0), Color(0xFF0C3A3E)),
    MemberColor('tijolo', Color(0xFFB23B3B), Color(0xFFFFB3AD), Color(0xFFFADCDA), Color(0xFF5A1E1E)),
    MemberColor('indigo', Color(0xFF4A55B0), Color(0xFFBCC2FF), Color(0xFFE1E3FA), Color(0xFF262C5E)),
    MemberColor('ameixa', Color(0xFF7B3F8C), Color(0xFFE7B3F5), Color(0xFFF3DEF8), Color(0xFF41204B)),
  ];

  /// Os hex da paleta antiga e a cor nova que os substitui.
  static const _legacy = {
    '4F7CAC': 'azul',
    'E07A5F': 'laranja',
    '81B29A': 'oliva',
    'F2CC8F': 'mostarda',
    '9C6ADE': 'roxo',
    '3D9970': 'verde',
    'D96C9C': 'rosa',
    '5BC0BE': 'petroleo',
  };

  static final Map<String, MemberColor> _byKey = {for (final c in colors) c.key: c};

  static final Map<String, MemberColor> _byHex = {
    for (final c in colors) _hex(c.strong): c,
    for (final entry in _legacy.entries) entry.key: _byKey[entry.value]!,
  };

  static String _hex(Color color) => (color.toARGB32() & 0xFFFFFF).toRadixString(16).padLeft(6, '0').toUpperCase();

  /// A cor de um membro a partir do que o servidor guarda (hex, novo ou antigo) ou de uma chave. Um hex que não é
  /// da paleta usa-se tal como vem, com as iniciais na cor que contrasta mais.
  static MemberColor resolve(String hexOrKey) {
    final normalized = hexOrKey.replaceFirst('#', '').toUpperCase();
    final known = _byHex[normalized] ?? _byKey[hexOrKey.toLowerCase()];
    if (known != null) return known;
    final value = int.tryParse(normalized, radix: 16);
    if (value == null) return colors.first;
    final color = Color(0xFF000000 | value);
    return MemberColor(normalized, color, color, color.withValues(alpha: 0.18), color.withValues(alpha: 0.3));
  }

  /// Os tons de uma cor de membro para o brilho do tema.
  static MemberTones tones(String hexOrKey, Brightness brightness) {
    final color = resolve(hexOrKey);
    final dark = brightness == Brightness.dark;
    final fill = dark ? color.light : color.strong;
    return MemberTones(
      fill: fill,
      onFill: _readableOn(fill),
      soft: dark ? color.softDark : color.softLight,
      onSoft: dark ? KubicoPalette.dark.onSurface : KubicoPalette.light.onSurface,
    );
  }

  /// Branco ou o castanho do texto, o que contrastar mais com o fundo.
  static Color _readableOn(Color background) {
    const white = Color(0xFFFFFFFF);
    const ink = Color(0xFF1B1512);
    return contrastRatio(white, background) >= contrastRatio(ink, background) ? white : ink;
  }
}

/// O contraste WCAG entre duas cores (1 a 21).
double contrastRatio(Color a, Color b) {
  final la = a.computeLuminance();
  final lb = b.computeLuminance();
  final (light, dark) = la > lb ? (la, lb) : (lb, la);
  return (light + 0.05) / (dark + 0.05);
}

/// As cores que o Material 3 não tem (sucesso, aviso, informação) e as das áreas, como extensão do tema.
@immutable
class KubicoColors extends ThemeExtension<KubicoColors> {
  const KubicoColors({
    required this.success,
    required this.successContainer,
    required this.onSuccessContainer,
    required this.warning,
    required this.warningContainer,
    required this.onWarningContainer,
    required this.info,
    required this.infoContainer,
    required this.onInfoContainer,
    required this.areas,
  });

  static const light = KubicoColors(
    success: Color(0xFF2E7D32),
    successContainer: Color(0xFFC8E6C9),
    onSuccessContainer: Color(0xFF0D3B12),
    warning: Color(0xFF8A5300),
    warningContainer: Color(0xFFFFE0B2),
    onWarningContainer: Color(0xFF3D2600),
    info: Color(0xFF1F5F99),
    infoContainer: Color(0xFFD3E4FF),
    onInfoContainer: Color(0xFF0A2E52),
    areas: {
      KubicoArea.today: AreaColors(Color(0xFF8C4A2F), Color(0xFFFFDCCB), Color(0xFF3A1406)),
      KubicoArea.shopping: AreaColors(Color(0xFF1F6A6A), Color(0xFFCDEBEA), Color(0xFF002E2E)),
      KubicoArea.chores: AreaColors(Color(0xFF3B5F8F), Color(0xFFD8E3F5), Color(0xFF0D2747)),
      KubicoArea.calendar: AreaColors(Color(0xFF66558F), Color(0xFFE6DEF7), Color(0xFF251A47)),
      KubicoArea.rewards: AreaColors(Color(0xFF7A5600), Color(0xFFFBE3A6), Color(0xFF2E2000)),
    },
  );

  static const dark = KubicoColors(
    success: Color(0xFF8FD694),
    successContainer: Color(0xFF1B4D20),
    onSuccessContainer: Color(0xFFC8E6C9),
    warning: Color(0xFFFFB960),
    warningContainer: Color(0xFF5C3A00),
    onWarningContainer: Color(0xFFFFE0B2),
    info: Color(0xFF9CCBFF),
    infoContainer: Color(0xFF13406B),
    onInfoContainer: Color(0xFFD3E4FF),
    areas: {
      KubicoArea.today: AreaColors(Color(0xFFFFB696), Color(0xFF6E3520), Color(0xFFFFDCCB)),
      KubicoArea.shopping: AreaColors(Color(0xFF8AD3D1), Color(0xFF0F4F4F), Color(0xFFCDEBEA)),
      KubicoArea.chores: AreaColors(Color(0xFFA9C7F2), Color(0xFF24446E), Color(0xFFD8E3F5)),
      KubicoArea.calendar: AreaColors(Color(0xFFCBBEF2), Color(0xFF4A3D72), Color(0xFFE6DEF7)),
      KubicoArea.rewards: AreaColors(Color(0xFFE9C46A), Color(0xFF574000), Color(0xFFFBE3A6)),
    },
  );

  final Color success;
  final Color successContainer;
  final Color onSuccessContainer;
  final Color warning;
  final Color warningContainer;
  final Color onWarningContainer;
  final Color info;
  final Color infoContainer;
  final Color onInfoContainer;
  final Map<KubicoArea, AreaColors> areas;

  AreaColors area(KubicoArea area) => areas[area]!;

  @override
  KubicoColors copyWith({
    Color? success,
    Color? successContainer,
    Color? onSuccessContainer,
    Color? warning,
    Color? warningContainer,
    Color? onWarningContainer,
    Color? info,
    Color? infoContainer,
    Color? onInfoContainer,
    Map<KubicoArea, AreaColors>? areas,
  }) => KubicoColors(
    success: success ?? this.success,
    successContainer: successContainer ?? this.successContainer,
    onSuccessContainer: onSuccessContainer ?? this.onSuccessContainer,
    warning: warning ?? this.warning,
    warningContainer: warningContainer ?? this.warningContainer,
    onWarningContainer: onWarningContainer ?? this.onWarningContainer,
    info: info ?? this.info,
    infoContainer: infoContainer ?? this.infoContainer,
    onInfoContainer: onInfoContainer ?? this.onInfoContainer,
    areas: areas ?? this.areas,
  );

  @override
  KubicoColors lerp(KubicoColors? other, double t) => t < 0.5 || other == null ? this : other;
}

/// Espaçamentos (grelha de 4), raios e durações.
abstract final class KubicoSpace {
  static const s4 = 4.0;
  static const s8 = 8.0;
  static const s12 = 12.0;
  static const s16 = 16.0;
  static const s24 = 24.0;
  static const s32 = 32.0;
  static const s48 = 48.0;
}

abstract final class KubicoRadius {
  static const xs = 6.0;
  static const sm = 10.0;
  static const field = 12.0;
  static const md = 16.0;
  static const lg = 24.0;
  static const xl = 32.0;
}

abstract final class KubicoMotion {
  static const short = Duration(milliseconds: 150);
  static const medium = Duration(milliseconds: 250);
  static const long = Duration(milliseconds: 400);

  /// A duração [d], ou zero quando a pessoa pediu menos movimento.
  static Duration of(BuildContext context, Duration d) => MediaQuery.disableAnimationsOf(context) ? Duration.zero : d;
}

extension KubicoThemeX on BuildContext {
  /// As cores extra do tema (as do Kubico por omissão, num tema que não as tenha, como o dos testes).
  KubicoColors get kubico {
    final theme = Theme.of(this);
    return theme.extension<KubicoColors>() ??
        (theme.brightness == Brightness.dark ? KubicoColors.dark : KubicoColors.light);
  }

  /// Os tons da cor de um membro (o hex que vem do servidor) para o tema atual.
  MemberTones memberTones(String hexOrKey) => MemberPalette.tones(hexOrKey, Theme.of(this).brightness);
}
