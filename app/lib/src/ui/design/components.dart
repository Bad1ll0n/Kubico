import 'package:flutter/material.dart';

import '../../l10n/strings.dart';
import 'tokens.dart';

/// A primeira letra de um nome (o primeiro carácter visível, não a primeira unidade UTF-16: "Élia", "🦁 Rui").
String initialOf(String name) {
  final trimmed = name.trim();
  return trimmed.isEmpty ? '?' : trimmed.characters.first.toUpperCase();
}

/// O avatar de um membro: a inicial na cor dele, sempre com o nome para os leitores de ecrã (a cor nunca vai
/// sozinha). O círculo é um ícone, não texto corrido: a letra não cresce com o tamanho de texto do sistema (o
/// círculo não cresceria com ela). [away] mostra um selo de avião (a pessoa está fora).
class MemberAvatar extends StatelessWidget {
  const MemberAvatar({required this.name, required this.color, this.size = 32, this.away = false, super.key});

  final String name;

  /// O hex que vem do servidor (ou uma chave da paleta).
  final String color;
  final double size;
  final bool away;

  @override
  Widget build(BuildContext context) {
    final tones = context.memberTones(color);
    final avatar = Container(
      width: size,
      height: size,
      alignment: Alignment.center,
      decoration: BoxDecoration(color: tones.fill, shape: BoxShape.circle),
      child: Text(
        initialOf(name),
        textScaler: TextScaler.noScaling,
        style: TextStyle(
          fontFamily: 'Nunito',
          fontWeight: FontWeight.w800,
          fontSize: size * 0.42,
          height: 1,
          color: tones.onFill,
        ),
      ),
    );
    return Semantics(
      label: away ? '$name (fora)'.tr : name,
      excludeSemantics: true,
      child: away ? _Away(size: size, child: avatar) : avatar,
    );
  }
}

class _Away extends StatelessWidget {
  const _Away({required this.size, required this.child});

  final double size;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final badge = size * 0.45;
    return SizedBox(
      width: size,
      height: size,
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Opacity(opacity: 0.6, child: child),
          Positioned(
            right: -badge * 0.2,
            bottom: -badge * 0.2,
            child: Container(
              width: badge,
              height: badge,
              decoration: BoxDecoration(
                color: scheme.surfaceContainerHighest,
                shape: BoxShape.circle,
                border: Border.all(color: scheme.surface, width: 1.5),
              ),
              child: Icon(Icons.flight_takeoff_rounded, size: badge * 0.62, color: scheme.onSurfaceVariant),
            ),
          ),
        ],
      ),
    );
  }
}

/// O cabeçalho de uma secção: título forte, contador numa pílula, ícone opcional (da cor da área ou do estado) e
/// uma ação à direita.
class SectionHeader extends StatelessWidget {
  const SectionHeader(this.title, {this.count, this.icon, this.color, this.action, super.key});

  final String title;
  final int? count;
  final IconData? icon;
  final Color? color;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: KubicoSpace.s16, bottom: KubicoSpace.s8),
      child: Row(
        children: [
          // O título, o ícone e o contador ocupam o que a ação deixa; o título só se corta se não couber mesmo.
          Expanded(
            child: Row(
              children: [
                if (icon != null) ...[
                  Icon(icon, size: 22, color: color ?? theme.colorScheme.onSurfaceVariant),
                  const SizedBox(width: KubicoSpace.s8),
                ],
                Flexible(
                  child: Text(
                    title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    // No telemóvel, um pouco mais pequeno: o título, o contador e a ação cabem numa linha.
                    style: theme.textTheme.titleLarge?.copyWith(
                      color: color ?? theme.colorScheme.onSurface,
                      fontSize: MediaQuery.sizeOf(context).width < 600 ? 19 : null,
                    ),
                  ),
                ),
                if (count != null) ...[
                  const SizedBox(width: KubicoSpace.s8),
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                    decoration: BoxDecoration(
                      color: theme.colorScheme.surfaceContainerHighest,
                      borderRadius: BorderRadius.circular(999),
                    ),
                    child: Text(
                      '$count',
                      style: theme.textTheme.labelMedium?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ),
                ],
              ],
            ),
          ),
          ?action,
        ],
      ),
    );
  }
}

/// Pontos numa pílula com uma estrela ("⭐ 5"). Um saldo negativo é uma dívida ("deve 5"), em cor de aviso e nunca
/// no vermelho de erro: não é um erro da criança.
class PointsBadge extends StatelessWidget {
  const PointsBadge(this.points, {this.large = false, super.key});

  final int points;
  final bool large;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final debt = points < 0;
    final (background, foreground) = debt
        ? (context.kubico.warningContainer, context.kubico.onWarningContainer)
        : (theme.colorScheme.tertiaryContainer, theme.colorScheme.onTertiaryContainer);
    final style = (large ? theme.textTheme.displaySmall : theme.textTheme.labelMedium)?.copyWith(
      color: foreground,
      fontFeatures: const [FontFeature.tabularFigures()],
    );
    return Semantics(
      label: debt ? 'deve ${-points} pontos'.tr : '$points pontos'.tr,
      excludeSemantics: true,
      child: Container(
        padding: EdgeInsets.symmetric(horizontal: large ? 16 : 8, vertical: large ? 8 : 3),
        decoration: BoxDecoration(color: background, borderRadius: BorderRadius.circular(999)),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              debt ? Icons.error_outline_rounded : Icons.star_rounded,
              size: large ? 32 : 16,
              color: debt ? context.kubico.warning : theme.colorScheme.tertiary,
            ),
            SizedBox(width: large ? 8 : 4),
            Text(debt ? 'deve ${-points}'.tr : '$points', style: style),
          ],
        ),
      ),
    );
  }
}

/// Um estado sem conteúdo (vazio, erro, sem acesso): ícone grande na cor da área, título, mensagem e uma ação.
/// Substitui as frases soltas ("Sem tarefas por fazer.") por algo que convida a agir.
class StateView extends StatelessWidget {
  const StateView({
    required this.icon,
    required this.title,
    this.message,
    this.action,
    this.color,
    this.inList = false,
    super.key,
  });

  /// O erro de um pedido: "sem ligação" (nuvem) distingue-se dos outros.
  factory StateView.error({required String message, required VoidCallback onRetry, bool offline = false, Key? key}) =>
      StateView(
        key: key,
        icon: offline ? Icons.cloud_off_rounded : Icons.error_outline_rounded,
        title: offline ? 'Sem ligação'.tr : 'Algo correu mal'.tr,
        message: message,
        action: FilledButton.tonalIcon(
          onPressed: onRetry,
          icon: const Icon(Icons.refresh_rounded),
          label: Text('Tentar de novo'.tr),
        ),
      );

  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;
  final Color? color;

  /// Dentro de uma lista que já se desloca (sem centrar na altura nem se deslocar sozinho).
  final bool inList;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final tint = color ?? theme.colorScheme.primary;
    final content = Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 360),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ExcludeSemantics(
              child: Container(
                width: 96,
                height: 96,
                decoration: BoxDecoration(color: tint.withValues(alpha: 0.14), shape: BoxShape.circle),
                child: Icon(icon, size: 48, color: tint),
              ),
            ),
            const SizedBox(height: KubicoSpace.s16),
            Text(title, style: theme.textTheme.titleLarge, textAlign: TextAlign.center),
            if (message != null) ...[
              const SizedBox(height: KubicoSpace.s8),
              Text(
                message!,
                style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                textAlign: TextAlign.center,
              ),
            ],
            if (action != null) ...[const SizedBox(height: KubicoSpace.s24), action!],
          ],
        ),
      ),
    );
    if (inList) return Padding(padding: const EdgeInsets.all(KubicoSpace.s32), child: content);
    return Center(
      child: SingleChildScrollView(padding: const EdgeInsets.all(KubicoSpace.s32), child: content),
    );
  }
}

/// Uma estrela que cresce e se desvanece no centro do ecrã com "+N": a recompensa visual de concluir uma tarefa
/// com pontos (para as crianças, a mensagem em texto não chega). Não leva texto que se leia como mensagem (essa
/// continua no SnackBar) e, com menos movimento pedido, não aparece.
void celebratePoints(BuildContext context, int points) {
  if (points <= 0 || MediaQuery.disableAnimationsOf(context)) return;
  final overlay = Overlay.maybeOf(context, rootOverlay: true);
  if (overlay == null) return;
  late final OverlayEntry entry;
  entry = OverlayEntry(
    builder: (_) => _Celebration(points: points, onDone: () => entry.remove()),
  );
  overlay.insert(entry);
}

class _Celebration extends StatefulWidget {
  const _Celebration({required this.points, required this.onDone});

  final int points;
  final VoidCallback onDone;

  @override
  State<_Celebration> createState() => _CelebrationState();
}

class _CelebrationState extends State<_Celebration> with SingleTickerProviderStateMixin {
  late final _controller = AnimationController(vsync: this, duration: const Duration(milliseconds: 1100))
    ..addStatusListener((status) {
      if (status == AnimationStatus.completed) widget.onDone();
    })
    ..forward();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    // Entra a crescer (com um pequeno ressalto) e sai a desvanecer.
    final scale = CurvedAnimation(
      parent: _controller,
      curve: const Interval(0, 0.45, curve: Curves.easeOutBack),
    );
    final fade = Tween<double>(begin: 1, end: 0).animate(
      CurvedAnimation(
        parent: _controller,
        curve: const Interval(0.7, 1, curve: Curves.easeIn),
      ),
    );
    return IgnorePointer(
      child: ExcludeSemantics(
        child: Center(
          child: FadeTransition(
            opacity: fade,
            child: ScaleTransition(
              scale: scale,
              child: Container(
                width: 160,
                height: 160,
                decoration: BoxDecoration(color: scheme.tertiaryContainer, shape: BoxShape.circle),
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Icon(Icons.star_rounded, size: 72, color: scheme.tertiary),
                    Text(
                      '+${widget.points}',
                      textScaler: TextScaler.noScaling,
                      style: Theme.of(context).textTheme.headlineMedium?.copyWith(color: scheme.onTertiaryContainer),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
