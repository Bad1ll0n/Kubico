import 'package:flutter/material.dart';

import 'theme.dart';

/// Abre um ecrã que vive numa biblioteca diferida (`import ... deferred as x`): na web, o código desse ecrã só se
/// descarrega na primeira vez que se abre, e o arranque fica mais leve (F4-17). Nas outras plataformas [load] não
/// faz nada. Se a descarga falhar (sem rede), mostra o erro em vez de abrir.
Future<T?> pushDeferred<T>(
  BuildContext context,
  Future<void> Function() load,
  Widget Function() build, {
  bool fullscreenDialog = false,
}) async {
  final navigator = Navigator.of(context);
  try {
    await load();
  } catch (e) {
    if (context.mounted) showError(context, e);
    return null;
  }
  return navigator.push<T>(MaterialPageRoute(fullscreenDialog: fullscreenDialog, builder: (_) => build()));
}
