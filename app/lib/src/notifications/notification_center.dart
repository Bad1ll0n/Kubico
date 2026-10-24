import 'dart:async';

import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';

/// Os avisos da pessoa com sessão iniciada: a lista, quantos estão por ler (para o sino) e os que chegam em
/// tempo real pelo canal pessoal.
class NotificationCenter extends ChangeNotifier {
  NotificationCenter({required this.api, required HouseholdRealtime realtime}) {
    _subscriptions
      ..add(realtime.events.listen(_onEvent))
      ..add(realtime.reconnected.listen((_) => load()));
  }

  final ApiClient api;
  final _subscriptions = <StreamSubscription<Object?>>[];

  List<AppNotification> items = const [];
  int unreadCount = 0;

  Future<void> load() async {
    try {
      final (loaded, unread) = await api.getNotifications();
      items = loaded;
      unreadCount = unread;
      notifyListeners();
    } on ApiException catch (e) {
      // O sino é secundário: sem rede fica como estava.
      debugPrint('Kubico: avisos indisponíveis (${e.message})');
    }
  }

  Future<void> markRead(AppNotification notification) async {
    if (notification.isRead) return;
    _apply((n) => n.id == notification.id);
    await api.markNotificationsRead([notification.id]);
  }

  Future<void> markAllRead() async {
    if (unreadCount == 0) return;
    _apply((_) => true);
    await api.markNotificationsRead();
  }

  /// Ao terminar a sessão.
  void clear() {
    items = const [];
    unreadCount = 0;
    notifyListeners();
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    super.dispose();
  }

  void _onEvent(HouseholdEvent event) {
    if (event case NotificationReceived(:final notification) when items.every((n) => n.id != notification.id)) {
      items = [notification, ...items];
      if (!notification.isRead) unreadCount++;
      notifyListeners();
    }
  }

  void _apply(bool Function(AppNotification n) which) {
    items = [for (final n in items) which(n) ? n.read() : n];
    unreadCount = items.where((n) => !n.isRead).length;
    notifyListeners();
  }
}
