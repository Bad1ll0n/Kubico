import 'package:flutter/material.dart';

/// As cores do símbolo: o telhado terracota e o creme da casa.
abstract final class KubicoBrand {
  static const terracotta = Color(0xFFB4472F);
  static const cream = Color(0xFFFFF8F3);
  static const night = Color(0xFF1B1512);
}

/// O símbolo do Kubico ("casa" em Angola): uma casinha creme com telhado e chaminé sobre terracota. O mesmo desenho
/// serve para o logótipo na app e para os ícones das plataformas (`tool/brand_icons_test.dart`), por isso não
/// depende de imagens.
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
  const KubicoMarkPainter({this.fullBleed = false, this.background = KubicoBrand.terracotta});

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
    final cream = Paint()..color = KubicoBrand.cream;

    // Chaminé, por baixo do telhado.
    canvas.drawRRect(
      RRect.fromRectAndRadius(Rect.fromPoints(p(0.615, 0.27), p(0.695, 0.42)), Radius.circular(scale * 0.015)),
      cream,
    );

    // Telhado: um "V" invertido grosso, com as pontas redondas.
    final roof = Path()
      ..moveTo(p(0.2, 0.52).dx, p(0.2, 0.52).dy)
      ..lineTo(p(0.5, 0.25).dx, p(0.5, 0.25).dy)
      ..lineTo(p(0.8, 0.52).dx, p(0.8, 0.52).dy);
    canvas.drawPath(
      roof,
      Paint()
        ..color = KubicoBrand.cream
        ..style = PaintingStyle.stroke
        ..strokeWidth = scale * 0.09
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round,
    );

    // Corpo da casa e a porta.
    canvas.drawRRect(
      RRect.fromRectAndRadius(Rect.fromPoints(p(0.3, 0.5), p(0.7, 0.78)), Radius.circular(scale * 0.06)),
      cream,
    );
    canvas.drawRRect(
      RRect.fromRectAndCorners(
        Rect.fromPoints(p(0.445, 0.6), p(0.555, 0.78)),
        topLeft: Radius.circular(scale * 0.055),
        topRight: Radius.circular(scale * 0.055),
      ),
      Paint()..color = background,
    );
  }

  @override
  bool shouldRepaint(KubicoMarkPainter oldDelegate) =>
      oldDelegate.fullBleed != fullBleed || oldDelegate.background != background;
}
