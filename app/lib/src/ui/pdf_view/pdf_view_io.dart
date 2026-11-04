import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/widgets.dart';
import 'package:share_plus/share_plus.dart';
import 'package:url_launcher/url_launcher.dart';

/// Fora da web não há visualizador dentro da app: o ecrã oferece abrir no do sistema.
bool get canShowPdfInline => false;

Widget inlinePdf(Uint8List bytes, String name) => const SizedBox.shrink();

/// No telemóvel, a folha de partilha ("Abrir com…"); no computador, a aplicação de PDF do sistema.
Future<void> openPdfExternally(Uint8List bytes, String name) async {
  if (Platform.isAndroid || Platform.isIOS) {
    await SharePlus.instance.share(
      ShareParams(
        files: [XFile.fromData(bytes, mimeType: 'application/pdf', name: name)],
        fileNameOverrides: [name],
      ),
    );
    return;
  }
  final folder = await Directory.systemTemp.createTemp('kubico');
  final file = File('${folder.path}${Platform.pathSeparator}${_safe(name)}');
  await file.writeAsBytes(bytes, flush: true);
  if (!await launchUrl(Uri.file(file.path))) {
    throw StateError('Não há uma aplicação para abrir PDF.');
  }
}

/// Só letras, algarismos, espaços, pontos, hífens e sublinhados (o nome vem de quem enviou o ficheiro).
String _safe(String name) {
  final clean = name.replaceAll(RegExp(r'[^\w .\-]', unicode: true), '_').trim();
  final base = clean.isEmpty || clean.startsWith('.') ? 'documento$clean' : clean;
  return base.toLowerCase().endsWith('.pdf') ? base : '$base.pdf';
}
