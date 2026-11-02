// Gera os ícones de todas as plataformas a partir do símbolo (KubicoMarkPainter). Não corre no CI: está em tool/.
// Para os voltar a gerar depois de mudar o desenho:
//   flutter test tool/brand_icons_test.dart
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/ui/design/brand.dart';

Future<Uint8List> _png(int size, {bool fullBleed = false}) async {
  final recorder = ui.PictureRecorder();
  final canvas = Canvas(recorder);
  KubicoMarkPainter(fullBleed: fullBleed).paint(canvas, Size.square(size.toDouble()));
  final image = await recorder.endRecording().toImage(size, size);
  final data = await image.toByteData(format: ui.ImageByteFormat.png);
  return data!.buffer.asUint8List();
}

/// Um .ico com as imagens em PNG (o Windows aceita-as desde o Vista).
Uint8List _ico(List<(int, Uint8List)> images) {
  final header = BytesBuilder()
    ..add(_u16(0))
    ..add(_u16(1))
    ..add(_u16(images.length));
  var offset = 6 + 16 * images.length;
  final entries = BytesBuilder();
  for (final (size, png) in images) {
    entries
      ..addByte(size >= 256 ? 0 : size)
      ..addByte(size >= 256 ? 0 : size)
      ..addByte(0)
      ..addByte(0)
      ..add(_u16(1))
      ..add(_u16(32))
      ..add(_u32(png.length))
      ..add(_u32(offset));
    offset += png.length;
  }
  return (BytesBuilder()
        ..add(header.toBytes())
        ..add(entries.toBytes())
        ..add([for (final (_, png) in images) ...png]))
      .toBytes();
}

List<int> _u16(int v) => [v & 0xFF, (v >> 8) & 0xFF];

List<int> _u32(int v) => [v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF, (v >> 24) & 0xFF];

void main() {
  test('gera os ícones', () async {
    Future<void> write(String path, int size, {bool fullBleed = false}) async =>
        File(path).writeAsBytesSync(await _png(size, fullBleed: fullBleed));

    // Web.
    await write('web/favicon.png', 32);
    await write('web/icons/Icon-192.png', 192);
    await write('web/icons/Icon-512.png', 512);
    await write('web/icons/Icon-maskable-192.png', 192, fullBleed: true);
    await write('web/icons/Icon-maskable-512.png', 512, fullBleed: true);

    // Android (ícone com o quadrado arredondado; o sistema recorta-o conforme o fabricante).
    for (final (folder, size) in [('mdpi', 48), ('hdpi', 72), ('xhdpi', 96), ('xxhdpi', 144), ('xxxhdpi', 192)]) {
      await write('android/app/src/main/res/mipmap-$folder/ic_launcher.png', size);
    }

    // iOS e macOS (o iOS recorta os cantos; o ícone vai com o fundo inteiro).
    const ios = 'ios/Runner/Assets.xcassets/AppIcon.appiconset';
    for (final file in Directory(ios).listSync().whereType<File>().where((f) => f.path.endsWith('.png'))) {
      final name = file.uri.pathSegments.last;
      final match = RegExp(r'Icon-App-([\d.]+)x[\d.]+@(\d)x\.png').firstMatch(name)!;
      final size = (double.parse(match.group(1)!) * int.parse(match.group(2)!)).round();
      await write(file.path, size, fullBleed: true);
    }
    for (final size in [16, 32, 64, 128, 256, 512, 1024]) {
      await write('macos/Runner/Assets.xcassets/AppIcon.appiconset/app_icon_$size.png', size);
    }

    // Windows.
    File('windows/runner/resources/app_icon.ico').writeAsBytesSync(
      _ico([
        for (final size in [16, 24, 32, 48, 64, 256]) (size, await _png(size)),
      ]),
    );
  });
}
