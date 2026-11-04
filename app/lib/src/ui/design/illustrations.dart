import 'dart:math' as math;

import 'package:flutter/material.dart';

/// As ilustrações "spot" dos estados vazios (07, 2.6): planas, em recorte de papel com cantos arredondados, com dois
/// tons da cor da área e o papel (a superfície do tema). Desenham-se com um [CustomPainter] (sem SVG nem
/// dependências) e são decorativas: não vão para os leitores de ecrã.
enum KubicoIllustration { basket, chores, calendar, gift, offline, house }

class IllustrationView extends StatelessWidget {
  const IllustrationView(this.illustration, {required this.color, this.size = 140, super.key});

  final KubicoIllustration illustration;
  final Color color;
  final double size;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return ExcludeSemantics(
      child: CustomPaint(
        size: Size.square(size),
        painter: _IllustrationPainter(
          illustration,
          strong: color,
          soft: Color.alphaBlend(color.withValues(alpha: 0.22), scheme.surface),
          // No escuro o papel é a superfície mais clara, para se destacar do fundo.
          paper: scheme.brightness == Brightness.dark ? scheme.surfaceContainerHighest : scheme.surfaceContainerLowest,
          line: scheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _IllustrationPainter extends CustomPainter {
  _IllustrationPainter(
    this.illustration, {
    required this.strong,
    required this.soft,
    required this.paper,
    required this.line,
  });

  final KubicoIllustration illustration;
  final Color strong;
  final Color soft;
  final Color paper;
  final Color line;

  Paint _fill(Color c) => Paint()..color = c;
  Paint _stroke(Color c, double w) => Paint()
    ..color = c
    ..style = PaintingStyle.stroke
    ..strokeWidth = w
    ..strokeCap = StrokeCap.round
    ..strokeJoin = StrokeJoin.round;

  @override
  void paint(Canvas canvas, Size size) {
    // Desenha-se numa grelha de 120 × 120.
    canvas.scale(size.width / 120, size.height / 120);
    // O fundo: uma mancha suave, como papel recortado.
    canvas.drawCircle(const Offset(60, 64), 50, _fill(soft));
    switch (illustration) {
      case KubicoIllustration.basket:
        _basket(canvas);
      case KubicoIllustration.chores:
        _chores(canvas);
      case KubicoIllustration.calendar:
        _calendar(canvas);
      case KubicoIllustration.gift:
        _gift(canvas);
      case KubicoIllustration.offline:
        _offline(canvas);
      case KubicoIllustration.house:
        _house(canvas);
    }
  }

  void _basket(Canvas canvas) {
    // A asa, os artigos a espreitar e o cesto.
    canvas.drawArc(const Rect.fromLTWH(32, 22, 56, 56), math.pi, math.pi, false, _stroke(strong, 6));
    canvas.drawCircle(const Offset(48, 50), 11, _fill(paper));
    canvas.drawRRect(RRect.fromLTRBR(60, 34, 74, 56, const Radius.circular(5)), _fill(paper));
    canvas.drawRRect(RRect.fromLTRBR(60, 34, 74, 56, const Radius.circular(5)), _stroke(line, 1.5));
    canvas.drawCircle(const Offset(48, 50), 11, _stroke(line, 1.5));
    final body = Path()
      ..moveTo(26, 56)
      ..lineTo(94, 56)
      ..lineTo(86, 92)
      ..quadraticBezierTo(85, 96, 80, 96)
      ..lineTo(40, 96)
      ..quadraticBezierTo(35, 96, 34, 92)
      ..close();
    canvas.drawPath(body, _fill(strong));
    canvas.drawRRect(RRect.fromLTRBR(22, 52, 98, 62, const Radius.circular(5)), _fill(strong));
    for (final x in [46.0, 60.0, 74.0]) {
      canvas.drawLine(Offset(x, 68), Offset(x - 1, 88), _stroke(paper, 3));
    }
  }

  void _chores(Canvas canvas) {
    // Uma vassoura encostada e uma estrela.
    canvas.save();
    canvas.translate(46, 62);
    canvas.rotate(-0.35);
    canvas.drawRRect(RRect.fromLTRBR(-3, -42, 3, 14, const Radius.circular(3)), _fill(line));
    final brush = Path()
      ..moveTo(-10, 12)
      ..lineTo(10, 12)
      ..lineTo(18, 38)
      ..quadraticBezierTo(0, 44, -18, 38)
      ..close();
    canvas.drawPath(brush, _fill(strong));
    for (final x in [-8.0, 0.0, 8.0]) {
      canvas.drawLine(Offset(x * 0.7, 18), Offset(x * 1.4, 36), _stroke(paper, 2));
    }
    canvas.restore();
    canvas.drawPath(_star(const Offset(80, 46), 20, 9), _fill(paper));
    canvas.drawPath(_star(const Offset(80, 46), 20, 9), _stroke(strong, 3));
    canvas.drawCircle(const Offset(92, 78), 4, _fill(strong));
    canvas.drawCircle(const Offset(70, 86), 3, _fill(strong));
  }

  void _calendar(Canvas canvas) {
    // Um sol atrás de uma folha de calendário.
    canvas.drawCircle(const Offset(84, 34), 14, _fill(strong));
    for (var i = 0; i < 8; i++) {
      final a = i * math.pi / 4;
      canvas.drawLine(
        Offset(84 + math.cos(a) * 19, 34 + math.sin(a) * 19),
        Offset(84 + math.cos(a) * 24, 34 + math.sin(a) * 24),
        _stroke(strong, 3),
      );
    }
    final sheet = RRect.fromLTRBR(26, 40, 88, 98, const Radius.circular(10));
    canvas.drawRRect(sheet, _fill(paper));
    canvas.save();
    canvas.clipRRect(sheet);
    canvas.drawRect(const Rect.fromLTRB(26, 40, 88, 56), _fill(strong));
    canvas.restore();
    canvas.drawRRect(sheet, _stroke(line, 1.5));
    for (final x in [40.0, 74.0]) {
      canvas.drawLine(Offset(x, 34), Offset(x, 46), _stroke(line, 4));
    }
    for (var row = 0; row < 3; row++) {
      for (var col = 0; col < 4; col++) {
        final c = Offset(37 + col * 13.0, 66 + row * 11.0);
        canvas.drawCircle(c, 3, _fill(row == 1 && col == 2 ? strong : soft));
      }
    }
  }

  void _gift(Canvas canvas) {
    // Uma caixa com laço.
    canvas.drawRRect(RRect.fromLTRBR(30, 58, 90, 98, const Radius.circular(8)), _fill(strong));
    canvas.drawRRect(RRect.fromLTRBR(25, 46, 95, 60, const Radius.circular(6)), _fill(strong));
    canvas.drawRect(const Rect.fromLTRB(54, 46, 66, 98), _fill(paper));
    canvas.drawLine(const Offset(30, 60), const Offset(90, 60), _stroke(paper, 2));
    final bow = _stroke(strong, 6);
    canvas.drawOval(const Rect.fromLTRB(36, 28, 60, 46), bow);
    canvas.drawOval(const Rect.fromLTRB(60, 28, 84, 46), bow);
    canvas.drawCircle(const Offset(60, 44), 6, _fill(paper));
    canvas.drawCircle(const Offset(60, 44), 6, _stroke(strong, 3));
  }

  void _offline(Canvas canvas) {
    // Uma nuvem a dormir.
    // A união das partes, para o contorno ser só o de fora.
    final cloud = [
      Path()..addOval(const Rect.fromLTRB(38, 36, 82, 80)),
      Path()..addOval(const Rect.fromLTRB(64, 52, 98, 86)),
      Path()..addRRect(RRect.fromLTRBR(36, 66, 86, 92, const Radius.circular(13))),
    ].fold(Path()..addOval(const Rect.fromLTRB(22, 56, 58, 92)), (a, b) => Path.combine(PathOperation.union, a, b));
    canvas.drawPath(cloud, _fill(paper));
    canvas.drawPath(cloud, _stroke(strong, 3));
    // Olhos fechados.
    canvas.drawArc(const Rect.fromLTWH(46, 66, 10, 8), 0, math.pi, false, _stroke(line, 2.5));
    canvas.drawArc(const Rect.fromLTWH(66, 66, 10, 8), 0, math.pi, false, _stroke(line, 2.5));
    // "z z"
    final z = _stroke(strong, 3);
    for (final (o, s) in [(const Offset(86, 26), 10.0), (const Offset(98, 14), 7.0)]) {
      canvas.drawPath(
        Path()
          ..moveTo(o.dx, o.dy)
          ..lineTo(o.dx + s, o.dy)
          ..lineTo(o.dx, o.dy + s)
          ..lineTo(o.dx + s, o.dy + s),
        z,
      );
    }
  }

  void _house(Canvas canvas) {
    // Uma casa com telhado, porta e janela.
    canvas.drawRRect(RRect.fromLTRBR(34, 56, 86, 98, const Radius.circular(6)), _fill(paper));
    canvas.drawRRect(RRect.fromLTRBR(34, 56, 86, 98, const Radius.circular(6)), _stroke(line, 1.5));
    final roof = Path()
      ..moveTo(24, 60)
      ..lineTo(60, 26)
      ..lineTo(96, 60)
      ..close();
    canvas.drawPath(roof, _fill(strong));
    canvas.drawPath(roof, _stroke(strong, 6));
    canvas.drawRRect(RRect.fromLTRBR(52, 72, 68, 98, const Radius.circular(8)), _fill(strong));
    canvas.drawRRect(RRect.fromLTRBR(72, 66, 82, 76, const Radius.circular(3)), _fill(soft));
  }

  Path _star(Offset center, double outer, double inner) {
    final path = Path();
    for (var i = 0; i < 10; i++) {
      final r = i.isEven ? outer : inner;
      final a = -math.pi / 2 + i * math.pi / 5;
      final p = center + Offset(math.cos(a) * r, math.sin(a) * r);
      i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
    }
    return path..close();
  }

  @override
  bool shouldRepaint(_IllustrationPainter old) =>
      old.illustration != illustration || old.strong != strong || old.soft != soft || old.paper != paper;
}
