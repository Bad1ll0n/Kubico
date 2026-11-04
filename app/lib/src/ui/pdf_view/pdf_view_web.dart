import 'dart:js_interop';
import 'dart:typed_data';
import 'dart:ui_web' as ui_web;

import 'package:flutter/widgets.dart';
import 'package:web/web.dart' as web;

/// Na web, o PDF mostra-se dentro da app, num iframe com o visualizador do navegador (um URL blob: o ficheiro não
/// sai do aparelho; a CSP aceita `frame-src blob:`).
bool get canShowPdfInline => true;

var _views = 0;

Widget inlinePdf(Uint8List bytes, String name) {
  final url = _blobUrl(bytes);
  final viewType = 'kubico-pdf-${_views++}';
  ui_web.platformViewRegistry.registerViewFactory(viewType, (int _) {
    final frame = web.HTMLIFrameElement()
      ..src = url
      ..title = name
      ..style.border = 'none'
      ..style.width = '100%'
      ..style.height = '100%';
    return frame;
  });
  return HtmlElementView(viewType: viewType);
}

/// Num separador novo, com o visualizador do navegador.
Future<void> openPdfExternally(Uint8List bytes, String name) async {
  web.window.open(_blobUrl(bytes), '_blank');
}

String _blobUrl(Uint8List bytes) =>
    web.URL.createObjectURL(web.Blob([bytes.toJS].toJS, web.BlobPropertyBag(type: 'application/pdf')));
