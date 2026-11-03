import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'deferred.dart';
import 'scanner_screen.dart' deferred as scanner;

/// Há câmara para ler códigos? (O leitor não funciona no Windows nem no Linux.)
bool get scannerAvailable =>
    kIsWeb ||
    defaultTargetPlatform == TargetPlatform.android ||
    defaultTargetPlatform == TargetPlatform.iOS ||
    defaultTargetPlatform == TargetPlatform.macOS;

/// Lê um código com a câmara (o leitor é uma biblioteca diferida: na web só se descarrega quando se usa) e devolve o
/// que [accept] aceitar, ou null se se fechar.
Future<String?> scanCode(
  BuildContext context, {
  required String title,
  required String hint,
  required String? Function(String raw) accept,
}) => pushDeferred<String>(
  context,
  scanner.loadLibrary,
  () => scanner.ScannerScreen<String>(title: title, hint: hint, accept: accept),
);
