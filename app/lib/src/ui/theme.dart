import 'package:flutter/material.dart';

import '../api/api_client.dart';
import '../l10n/strings.dart';

/// Terracota quente, a cor de um telhado: a identidade do Kubico.
const kubicoSeed = Color(0xFFC8553D);

ThemeData buildTheme(Brightness brightness) {
  final scheme = ColorScheme.fromSeed(seedColor: kubicoSeed, brightness: brightness);
  return ThemeData(
    colorScheme: scheme,
    useMaterial3: true,
    visualDensity: VisualDensity.adaptivePlatformDensity,
    inputDecorationTheme: const InputDecorationTheme(border: OutlineInputBorder()),
    appBarTheme: AppBarTheme(backgroundColor: scheme.surface, surfaceTintColor: Colors.transparent, centerTitle: false),
    cardTheme: CardThemeData(
      elevation: 0,
      color: scheme.surfaceContainerLow,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
    ),
  );
}

Color colorFromHex(String hex) {
  final value = int.tryParse(hex.replaceFirst('#', ''), radix: 16) ?? 0x4F7CAC;
  return Color(0xFF000000 | value);
}

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
