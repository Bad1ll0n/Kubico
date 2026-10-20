import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../l10n/strings.dart';

/// Há câmara para ler códigos? (O leitor não funciona no Windows nem no Linux.)
bool get scannerAvailable =>
    kIsWeb ||
    defaultTargetPlatform == TargetPlatform.android ||
    defaultTargetPlatform == TargetPlatform.iOS ||
    defaultTargetPlatform == TargetPlatform.macOS;

/// Lê um código com a câmara (QR de um convite, código de barras de um produto) e devolve o que [accept] aceitar.
class ScannerScreen<T extends Object> extends StatefulWidget {
  const ScannerScreen({super.key, required this.title, required this.hint, required this.accept});

  final String title;

  /// O que apontar ("Aponta para o QR do convite").
  final String hint;

  /// O valor lido se servir, ou null para continuar à procura.
  final T? Function(String raw) accept;

  @override
  State<ScannerScreen<T>> createState() => _ScannerScreenState<T>();
}

class _ScannerScreenState<T extends Object> extends State<ScannerScreen<T>> {
  var _done = false;

  void _onDetect(BarcodeCapture capture) {
    if (_done) return;
    for (final barcode in capture.barcodes) {
      final value = barcode.rawValue == null ? null : widget.accept(barcode.rawValue!);
      if (value != null) {
        _done = true;
        Navigator.of(context).pop(value);
        return;
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text(widget.title)),
      body: Stack(
        children: [
          MobileScanner(
            onDetect: _onDetect,
            errorBuilder: (context, error) => Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text(
                  error.errorCode == MobileScannerErrorCode.permissionDenied
                      ? 'O Kubico não tem autorização para usar a câmara. Dá-a nas definições do aparelho.'.tr
                      : 'Não foi possível usar a câmara.'.tr,
                  textAlign: TextAlign.center,
                ),
              ),
            ),
          ),
          Positioned(
            left: 16,
            right: 16,
            bottom: 32,
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(12),
                child: Text(widget.hint, textAlign: TextAlign.center),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
