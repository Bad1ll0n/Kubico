import 'package:flutter/material.dart';

/// As cores do símbolo: o verde-salva da marca, o creme, a hortelã e o damasco (as mesmas da paleta, suaves para os
/// olhos).
abstract final class KubicoBrand {
  static const sage = Color(0xFF3E6B57);
  static const cream = Color(0xFFF7F5EF);
  static const mint = Color(0xFFA5D1BB);
  static const apricot = Color(0xFFE8956D);
  static const night = Color(0xFF161A17);
}

/// O símbolo do Kubico: um cubo ("kubico") em perspetiva, com as três faces em creme, hortelã e damasco, sobre o
/// verde-salva. O mesmo desenho serve para o logótipo na app e para os ícones das plataformas
/// (`tool/brand_icons_test.dart`), por isso não depende de imagens.
class KubicoMark extends StatelessWidget {
  const KubicoMark({this.size = 72, super.key});

  final double size;

  @override
  Widget build(BuildContext context) => Semantics(
    label: 'Kubico',
    image: true,
    child: CustomPaint(size: Size.square(size), painter: const KubicoMarkPainter()),
  );
}

/// Pinta o símbolo num quadrado. Com [fullBleed], o fundo ocupa tudo e o desenho fica na zona segura central (80 %),
/// como pedem os ícones "maskable" da web e os adaptativos do Android; sem ele, o fundo é um quadrado arredondado.
class KubicoMarkPainter extends CustomPainter {
  const KubicoMarkPainter({this.fullBleed = false, this.background = KubicoBrand.sage});

  final bool fullBleed;
  final Color background;

  @override
  void paint(Canvas canvas, Size size) {
    final side = size.shortestSide;
    final tile = Paint()..color = background;
    if (fullBleed) {
      canvas.drawRect(Offset.zero & size, tile);
    } else {
      canvas.drawRRect(RRect.fromRectAndRadius(Offset.zero & size, Radius.circular(side * 0.22)), tile);
    }

    // O desenho num quadrado de 1 × 1 (encolhido para a zona segura quando o fundo ocupa tudo).
    final scale = fullBleed ? side * 0.8 : side;
    final origin = fullBleed ? Offset(side * 0.1, side * 0.1) : Offset.zero;
    Offset p(double x, double y) => origin + Offset(x * scale, y * scale);
    Path face(List<Offset> points) => Path()..addPolygon(points, true);

    // O cubo: o vértice de cima, o centro (onde se juntam as três faces) e os cantos.
    final top = p(0.5, 0.2);
    final left = p(0.22, 0.36);
    final right = p(0.78, 0.36);
    final center = p(0.5, 0.52);
    final bottomLeft = p(0.22, 0.68);
    final bottomRight = p(0.78, 0.68);
    final bottom = p(0.5, 0.84);

    // Os cantos arredondados vêm de um traço da mesma cor à volta de cada face.
    void draw(List<Offset> points, Color color) {
      final path = face(points);
      canvas
        ..drawPath(path, Paint()..color = color)
        ..drawPath(
          path,
          Paint()
            ..color = color
            ..style = PaintingStyle.stroke
            ..strokeWidth = scale * 0.05
            ..strokeJoin = StrokeJoin.round,
        );
    }

    // A face de cima por último: tapa as junções das outras duas.
    draw([left, center, bottom, bottomLeft], KubicoBrand.cream);
    draw([center, right, bottomRight, bottom], KubicoBrand.apricot);
    draw([top, right, center, left], KubicoBrand.mint);

    // As arestas entre as faces, finas, na cor do fundo: o cubo lê-se mesmo pequeno (favicon).
    final edge = Paint()
      ..color = background
      ..style = PaintingStyle.stroke
      ..strokeWidth = scale * 0.022
      ..strokeCap = StrokeCap.butt;
    canvas
      ..drawLine(center, left, edge)
      ..drawLine(center, right, edge)
      ..drawLine(center, bottom, edge);
  }

  @override
  bool shouldRepaint(KubicoMarkPainter oldDelegate) =>
      oldDelegate.fullBleed != fullBleed || oldDelegate.background != background;
}
