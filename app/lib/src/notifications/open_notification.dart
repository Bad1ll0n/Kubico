import 'package:flutter/foundation.dart';

import '../api/models.dart';

/// Para onde leva tocar num aviso: a casa e a área (ou o ecrã dos membros, num perfil de criança bloqueado).
@immutable
class NotificationTarget {
  const NotificationTarget({required this.householdId, this.module, this.members = false});

  final String householdId;
  final HouseholdModule? module;

  /// O ecrã dos membros e convites (por exemplo, para mudar o PIN de uma criança bloqueada).
  final bool members;

  /// O destino de um aviso pelo tipo (o mesmo nome no servidor, `NotificationKind`, e no `data` do FCM).
  static NotificationTarget? of(String kind, String? householdId) {
    if (householdId == null || householdId.isEmpty) return null;
    return switch (kind) {
      'ChoreAssigned' ||
      'ChoreCompleted' ||
      'ChoreRejected' ||
      'ChoreReminder' ||
      'WeeklySummary' => NotificationTarget(householdId: householdId, module: HouseholdModule.chores),
      'EventAssigned' ||
      'RideAssigned' ||
      'EventReminder' => NotificationTarget(householdId: householdId, module: HouseholdModule.calendar),
      'RewardRequested' ||
      'RewardDecided' ||
      'AllowancePaid' ||
      'AllowanceDue' => NotificationTarget(householdId: householdId, module: HouseholdModule.rewards),
      'ChildPinLocked' => NotificationTarget(householdId: householdId, members: true),
      'HomeReminder' => NotificationTarget(householdId: householdId, module: HouseholdModule.home),
      _ => NotificationTarget(householdId: householdId),
    };
  }

  @override
  bool operator ==(Object other) =>
      other is NotificationTarget &&
      other.householdId == householdId &&
      other.module == module &&
      other.members == members;

  @override
  int get hashCode => Object.hash(householdId, module, members);
}

/// Pedidos para abrir um aviso (tocado no sino da app ou no telemóvel). O ecrã principal ouve-os: muda para a casa
/// e abre a área.
abstract final class OpenNotification {
  static final requested = ValueNotifier<NotificationTarget?>(null);

  static void open(NotificationTarget? target) {
    if (target == null) return;
    // Um novo pedido igual ao anterior também tem de chegar a quem ouve.
    requested.value = null;
    requested.value = target;
  }
}
