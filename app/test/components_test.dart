import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/storage/key_value_store.dart';
import 'package:kubico/src/ui/design/components.dart';
import 'package:kubico/src/ui/design/illustrations.dart';
import 'package:kubico/src/ui/design/reading_mode.dart';
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

  testWidgets('com menos movimento pedido não há estrela, mas o telemóvel vibra na mesma', (tester) async {
    final haptics = <String>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'HapticFeedback.vibrate') haptics.add(call.arguments as String);
      return null;
    });
    addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));
    await tester.pumpWidget(_app(reduceMotion: true));

    await tester.tap(find.text('Concluir'));
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('+5'), findsNothing);
    expect(haptics, ['HapticFeedbackType.mediumImpact']);
  });

  testWidgets('os pontos vão numa pílula com estrela; uma dívida diz "deve", sem estrela', (tester) async {
    await tester.pumpWidget(_app());

    expect(find.text('12'), findsOneWidget);
    expect(find.text('deve 3'), findsOneWidget);
    expect(find.byIcon(Icons.star_rounded), findsOneWidget);
  });

  testWidgets('o modo Conforto aumenta a letra por cima da do sistema (até 2×) e fica guardado no aparelho', (
    tester,
  ) async {
    final store = MemoryKeyValueStore();
    await ReadingMode.restore(store);
    addTearDown(() => ReadingMode.current.value = ReadingMode.normal);
    double? scale;
    Widget app(double system) => MaterialApp(
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(system)),
        child: Builder(builder: (context) => ReadingMode.current.value.apply(context, child!)),
      ),
      home: Builder(
        builder: (context) {
          scale = MediaQuery.textScalerOf(context).scale(10);
          return const SizedBox();
        },
      ),
    );

    await ReadingMode.set(ReadingMode.comfort);
    await tester.pumpWidget(app(1));
    expect(scale, closeTo(11.5, 0.001));

    await tester.pumpWidget(app(1.9));
    expect(scale, closeTo(20, 0.001), reason: 'no máximo o dobro');

    ReadingMode.current.value = ReadingMode.normal;
    await ReadingMode.restore(store);
    expect(ReadingMode.current.value, ReadingMode.comfort);
  });

  testWidgets('um estado vazio dentro de uma lista aguenta letra a 200 % num telemóvel pequeno', (tester) async {
    tester.view.physicalSize = const Size(320, 640);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(Brightness.light),
        home: MediaQuery(
          data: const MediaQueryData(size: Size(320, 640), textScaler: TextScaler.linear(2)),
          child: Scaffold(
            body: ListView(
              children: const [
                Text('Cabeçalho'),
                StateView(
                  inList: true,
                  icon: Icons.task_alt_rounded,
                  title: 'Sem tarefas por fazer',
                  message: 'Cria a primeira e escolhe quem a faz e quando se repete.',
                ),
              ],
            ),
          ),
        ),
      ),
    );

    expect(tester.takeException(), isNull);
    expect(find.text('Sem tarefas por fazer'), findsOneWidget);
  });

  testWidgets('as ilustrações desenham-se no claro e no escuro e não vão para os leitores de ecrã', (tester) async {
    for (final brightness in Brightness.values) {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(brightness),
          home: Scaffold(
            body: ListView(
              children: [
                for (final illustration in KubicoIllustration.values)
                  StateView(
                    inList: true,
                    icon: Icons.inbox_rounded,
                    illustration: illustration,
                    title: 'Vazio ${illustration.name}',
                  ),
              ],
            ),
          ),
        ),
      );
      expect(tester.takeException(), isNull);
      expect(find.byType(IllustrationView), findsWidgets);
      expect(find.byIcon(Icons.inbox_rounded), findsNothing, reason: 'a ilustração fica no lugar do ícone');
    }
    final semantics = tester.ensureSemantics();
    expect(find.bySemanticsLabel(RegExp('Vazio basket')), findsOneWidget);
    semantics.dispose();
  });
}
