import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/ui/design/components.dart';
import 'package:kubico/src/ui/theme.dart';

Widget _app({bool reduceMotion = false}) => MaterialApp(
  theme: buildTheme(Brightness.light),
  home: MediaQuery(
    data: MediaQueryData(disableAnimations: reduceMotion),
    child: Builder(
      builder: (context) => Scaffold(
        body: Column(
          children: [
            TextButton(onPressed: () => celebratePoints(context, 5), child: const Text('Concluir')),
            const PointsBadge(12),
            const PointsBadge(-3),
          ],
        ),
      ),
    ),
  ),
);

void main() {
  testWidgets('concluir com pontos mostra a estrela com "+5", que desaparece sozinha', (tester) async {
    await tester.pumpWidget(_app());

    await tester.tap(find.text('Concluir'));
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.text('+5'), findsOneWidget);

    await tester.pumpAndSettle();
    expect(find.text('+5'), findsNothing);
  });

  testWidgets('com menos movimento pedido não há estrela', (tester) async {
    await tester.pumpWidget(_app(reduceMotion: true));

    await tester.tap(find.text('Concluir'));
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('+5'), findsNothing);
  });

  testWidgets('os pontos vão numa pílula com estrela; uma dívida diz "deve", sem estrela', (tester) async {
    await tester.pumpWidget(_app());

    expect(find.text('12'), findsOneWidget);
    expect(find.text('deve 3'), findsOneWidget);
    expect(find.byIcon(Icons.star_rounded), findsOneWidget);
  });
}
