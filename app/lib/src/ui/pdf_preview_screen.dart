import 'package:file_saver/file_saver.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import '../l10n/strings.dart';
import 'design/components.dart';
import 'pdf_view/pdf_view.dart';
import 'theme.dart';

/// Ver um PDF da Casa (faturas, garantias e manuais). Na web abre-se dentro da app, com o visualizador do navegador;
/// nas outras plataformas, num botão que o abre no visualizador do sistema. Vive numa biblioteca diferida.
class PdfPreviewScreen extends StatefulWidget {
  const PdfPreviewScreen({required this.name, required this.load, super.key});

  final String name;
  final Future<Uint8List> Function() load;

  /// Nos testes, no lugar do visualizador do navegador.
  @visibleForTesting
  static Widget Function(Uint8List bytes, String name)? debugViewer;

  @override
  State<PdfPreviewScreen> createState() => _PdfPreviewScreenState();
}

class _PdfPreviewScreenState extends State<PdfPreviewScreen> {
  Uint8List? _bytes;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _fetch();
  }

  Future<void> _fetch() async {
    setState(() => _error = null);
    try {
      final bytes = await widget.load();
      if (mounted) setState(() => _bytes = bytes);
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _save() async {
    final bytes = _bytes;
    if (bytes == null) return;
    final name = widget.name.toLowerCase().endsWith('.pdf')
        ? widget.name.substring(0, widget.name.length - 4)
        : widget.name;
    try {
      await FileSaver.instance.saveFile(name: name, bytes: bytes, fileExtension: 'pdf', mimeType: MimeType.pdf);
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Guardado.'.tr)));
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _open(Uint8List data) async {
    try {
      await openPdfExternally(data, widget.name);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final bytes = _bytes;
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.name),
        actions: [
          if (bytes != null)
            IconButton(
              tooltip: 'Guardar no dispositivo'.tr,
              icon: const Icon(Icons.download_rounded),
              onPressed: _save,
            ),
        ],
      ),
      body: switch ((bytes, _error)) {
        (_, final Object error) => StateView.error(
          message: errorText(error),
          offline: isOffline(error),
          onRetry: _fetch,
        ),
        (null, _) => const Center(child: CircularProgressIndicator()),
        (final Uint8List data, _) =>
          PdfPreviewScreen.debugViewer?.call(data, widget.name) ??
              (canShowPdfInline
                  ? inlinePdf(data, widget.name)
                  : StateView(
                      icon: Icons.picture_as_pdf_rounded,
                      title: widget.name,
                      message: 'Abre-se no visualizador de PDF do aparelho.'.tr,
                      action: FilledButton.icon(
                        onPressed: () => _open(data),
                        icon: const Icon(Icons.open_in_new_rounded),
                        label: Text('Abrir o PDF'.tr),
                      ),
                    )),
      },
    );
  }
}
