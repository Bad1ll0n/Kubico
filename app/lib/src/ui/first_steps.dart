import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import '../l10n/strings.dart';
import '../storage/key_value_store.dart';
import 'design/components.dart';
import 'household_screen.dart';
import 'theme.dart';

/// O primeiro uso guiado (F4-06): depois de criar uma casa, três passos para a pôr a funcionar em menos de dois
/// minutos. O ecrã principal ouve [pending] e abre-o.
abstract final class FirstSteps {
  /// A casa acabada de criar (null quando não há nada para mostrar).
  static final pending = ValueNotifier<String?>(null);
}

class FirstStepsScreen extends StatelessWidget {
  const FirstStepsScreen({required this.householdId, super.key});

  final String householdId;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    Widget step(int n, IconData icon, String title, String text, {String? action, VoidCallback? onAction}) => Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            CircleAvatar(
              backgroundColor: theme.colorScheme.primaryContainer,
              foregroundColor: theme.colorScheme.onPrimaryContainer,
              child: Text('$n'),
            ),
            const SizedBox(width: 16),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(icon, size: 20),
                      const SizedBox(width: 8),
                      Expanded(child: Text(title, style: theme.textTheme.titleMedium)),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Text(text, style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
                  if (action != null) ...[
                    const SizedBox(height: 8),
                    FilledButton.tonal(onPressed: onAction, child: Text(action)),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );

    void openMembers() =>
        Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => HouseholdScreen(householdId: householdId)));

    return Scaffold(
      appBar: AppBar(title: Text('Primeiros passos'.tr)),
      body: ListView(
        padding: readablePadding(context, top: 16, bottom: 24),
        children: [
          Text('A casa está criada! Três passos e fica pronta:'.tr, style: theme.textTheme.titleLarge),
          const SizedBox(height: 16),
          step(
            1,
            Icons.qr_code_rounded,
            'Convida a família'.tr,
            'Mostra o QR ou partilha o convite: entram logo nesta casa.'.tr,
            action: 'Convidar'.tr,
            onAction: openMembers,
          ),
          step(
            2,
            Icons.child_care_rounded,
            'Cria os perfis das crianças'.tr,
            'Sem email: entram com o código da família e um PIN, e têm as suas rotinas.'.tr,
            action: 'Criar perfil'.tr,
            onAction: openMembers,
          ),
          step(
            3,
            Icons.add_to_home_screen_rounded,
            'Põe a app no ecrã inicial'.tr,
            kIsWeb
                ? 'No menu do browser, escolhe "Instalar app" ou "Adicionar ao ecrã principal".'.tr
                : 'Mantém o ícone do Kubico à mão: os avisos chegam aqui.'.tr,
          ),
          const SizedBox(height: 16),
          FilledButton(onPressed: () => Navigator.pop(context), child: Text('Começar'.tr)),
        ],
      ),
    );
  }
}

/// As dicas de primeira vez (F4-06): cada uma aparece até se fechar, uma vez por aparelho.
abstract final class Tips {
  static const _key = 'kubico.tips.seen';
  static final seen = ValueNotifier<Set<String>>({});
  static KeyValueStore? _store;

  static Future<void> restore(KeyValueStore store) async {
    _store = store;
    try {
      seen.value = {...?(await store.read(_key))?.split(',').where((s) => s.isNotEmpty)};
    } catch (_) {
      // Sem armazenamento: as dicas aparecem.
    }
  }

  static Future<void> dismiss(String id) async {
    seen.value = {...seen.value, id};
    try {
      await _store?.write(_key, seen.value.join(','));
    } catch (_) {
      // Volta a aparecer no próximo arranque.
    }
  }
}

/// Uma dica de primeira vez no topo de uma área, com "Percebi" para a fechar.
class FirstTimeTip extends StatelessWidget {
  const FirstTimeTip({required this.id, required this.text, super.key});

  final String id;
  final String text;

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<Set<String>>(
    valueListenable: Tips.seen,
    builder: (context, seen, _) {
      if (seen.contains(id)) return const SizedBox.shrink();
      final colors = context.kubico;
      return Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
        child: Material(
          color: colors.infoContainer,
          borderRadius: BorderRadius.circular(KubicoRadius.md),
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 8, 8),
            child: Row(
              children: [
                Icon(Icons.lightbulb_rounded, color: colors.onInfoContainer),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(text, style: TextStyle(color: colors.onInfoContainer)),
                ),
                TextButton(onPressed: () => Tips.dismiss(id), child: Text('Percebi'.tr)),
              ],
            ),
          ),
        ),
      );
    },
  );
}
