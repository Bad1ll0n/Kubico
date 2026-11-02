import 'package:flutter/material.dart';

import '../api/api_client.dart';
import '../l10n/strings.dart';
import 'design/tokens.dart';

export 'design/tokens.dart';

/// O tema do Kubico: as cores do design system (`design/tokens.dart`), Nunito nos títulos e números e Atkinson
/// Hyperlegible Next no texto (feita para baixa visão: distingue I, l e 1), densidade normal em todas as plataformas
/// (no Windows a "adaptativa" encolhia os alvos de toque para ~40 dp num tablet de cozinha).
ThemeData buildTheme(Brightness brightness) {
  final scheme = brightness == Brightness.dark ? KubicoPalette.dark : KubicoPalette.light;
  final colors = brightness == Brightness.dark ? KubicoColors.dark : KubicoColors.light;
  final text = _textTheme(scheme);
  final fieldShape = OutlineInputBorder(
    borderRadius: BorderRadius.circular(KubicoRadius.field),
    borderSide: BorderSide(color: scheme.outline),
  );
  return ThemeData(
    colorScheme: scheme,
    useMaterial3: true,
    fontFamily: kubicoTextFont,
    textTheme: text,
    extensions: [colors],
    scaffoldBackgroundColor: scheme.surface,
    visualDensity: VisualDensity.standard,
    materialTapTargetSize: MaterialTapTargetSize.padded,
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: scheme.surfaceContainer,
      border: fieldShape,
      enabledBorder: fieldShape.copyWith(borderSide: BorderSide(color: scheme.outlineVariant)),
      focusedBorder: fieldShape.copyWith(borderSide: BorderSide(color: scheme.primary, width: 2)),
    ),
    appBarTheme: AppBarTheme(
      backgroundColor: scheme.surface,
      surfaceTintColor: Colors.transparent,
      centerTitle: false,
      titleTextStyle: text.titleLarge?.copyWith(color: scheme.onSurface),
    ),
    cardTheme: CardThemeData(
      elevation: 0,
      color: scheme.surfaceContainerLow,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(KubicoRadius.md)),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: const Size(64, 48),
        shape: const StadiumBorder(),
        textStyle: text.labelLarge,
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: const Size(64, 48),
        shape: const StadiumBorder(),
        textStyle: text.labelLarge,
      ),
    ),
    textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(textStyle: text.labelLarge)),
    floatingActionButtonTheme: FloatingActionButtonThemeData(
      backgroundColor: scheme.primaryContainer,
      foregroundColor: scheme.onPrimaryContainer,
      extendedTextStyle: text.labelLarge,
    ),
    chipTheme: ChipThemeData(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(KubicoRadius.sm)),
      labelStyle: text.labelMedium,
    ),
    navigationBarTheme: NavigationBarThemeData(
      height: 72,
      labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
      backgroundColor: scheme.surfaceContainer,
      // 5 destinos num telemóvel de 360 dp: "Recompensas" tem de caber numa linha.
      labelTextStyle: WidgetStatePropertyAll(text.labelSmall?.copyWith(fontSize: 11, letterSpacing: -0.2)),
    ),
    navigationRailTheme: NavigationRailThemeData(
      backgroundColor: scheme.surfaceContainer,
      selectedLabelTextStyle: text.labelSmall?.copyWith(color: scheme.onSurface),
      unselectedLabelTextStyle: text.labelSmall?.copyWith(color: scheme.onSurfaceVariant),
    ),
    listTileTheme: ListTileThemeData(
      minVerticalPadding: 12,
      titleTextStyle: text.titleMedium?.copyWith(color: scheme.onSurface),
    ),
    snackBarTheme: SnackBarThemeData(
      behavior: SnackBarBehavior.floating,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(KubicoRadius.field)),
    ),
    dialogTheme: DialogThemeData(
      backgroundColor: scheme.surfaceContainerHigh,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(KubicoRadius.lg)),
      titleTextStyle: text.headlineSmall?.copyWith(color: scheme.onSurface),
    ),
    bottomSheetTheme: BottomSheetThemeData(
      backgroundColor: scheme.surfaceContainerHigh,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(KubicoRadius.lg))),
    ),
    dividerTheme: DividerThemeData(color: scheme.outlineVariant),
    progressIndicatorTheme: ProgressIndicatorThemeData(linearTrackColor: scheme.surfaceContainerHighest),
  );
}

/// A família dos títulos, botões e números.
const kubicoDisplayFont = 'Nunito';

/// A família do texto corrido.
const kubicoTextFont = 'AtkinsonHyperlegibleNext';

TextTheme _textTheme(ColorScheme scheme) {
  TextStyle display(double size, double height, FontWeight weight) =>
      TextStyle(fontFamily: kubicoDisplayFont, fontSize: size, height: height / size, fontWeight: weight);
  TextStyle body(double size, double height) =>
      TextStyle(fontFamily: kubicoTextFont, fontSize: size, height: height / size, fontWeight: FontWeight.w400);
  return TextTheme(
    displayLarge: display(57, 64, FontWeight.w800),
    displayMedium: display(45, 52, FontWeight.w800),
    displaySmall: display(36, 44, FontWeight.w700),
    headlineLarge: display(32, 40, FontWeight.w700),
    headlineMedium: display(28, 36, FontWeight.w700),
    headlineSmall: display(24, 32, FontWeight.w700),
    titleLarge: display(22, 28, FontWeight.w700),
    titleMedium: display(17, 24, FontWeight.w700),
    titleSmall: display(14, 20, FontWeight.w700),
    bodyLarge: body(17, 26),
    bodyMedium: body(15, 22),
    bodySmall: body(13, 18),
    labelLarge: display(15, 20, FontWeight.w700),
    labelMedium: display(13, 16, FontWeight.w700),
    labelSmall: display(12, 16, FontWeight.w700),
  ).apply(bodyColor: scheme.onSurface, displayColor: scheme.onSurface);
}

/// Algarismos todos da mesma largura (horas, pontos, quantidades alinham em coluna).
const tabularFigures = [FontFeature.tabularFigures()];

/// A cor de um membro (o hex que vem do servidor). Para avatares e chips usa-se o `MemberAvatar`, que escolhe também
/// a cor das iniciais.
Color memberColor(BuildContext context, String hex) => context.memberTones(hex).fill;

void showError(BuildContext context, Object error) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(errorText(error))));
}

/// O servidor recusou desfazer (ou não aceitar) uma tarefa cujos pontos já foram trocados: quem gere a área pode
/// continuar na mesma, e a pessoa fica a dever esses pontos. Devolve true se confirmou.
Future<bool> confirmPointsDebt(BuildContext context, ApiException error) async =>
    await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Os pontos já foram trocados'.tr),
        content: Text('${error.message} ${'Queres continuar mesmo assim?'.tr}'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Cancelar'.tr)),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text('Continuar mesmo assim'.tr)),
        ],
      ),
    ) ==
    true;

/// Um saldo de pontos: "12 pts", ou "deve 5 pts" quando ficou negativo (pontos trocados de uma tarefa que depois
/// não foi aceite).
String pointsText(int points) => points < 0 ? 'deve ${-points} pts'.tr : '$points pts';

/// A mensagem a mostrar para um erro: a do servidor (já traduzida), ou uma frase simples para os outros (uma
/// resposta que a app não percebe, por exemplo), em vez do texto técnico da exceção.
String errorText(Object error) {
  if (error is ApiException || error is TwoFactorRequired) return error.toString();
  debugPrint('Kubico: erro inesperado ($error)');
  return 'Ocorreu um erro inesperado. Tenta outra vez.'.tr;
}
