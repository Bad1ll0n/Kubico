import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:signalr_netcore/signalr_client.dart';

import '../api/models.dart';

/// Eventos recebidos em tempo real. Os nomes coincidem com HouseholdEvents no backend.
sealed class HouseholdEvent {
  const HouseholdEvent();
}

class ShoppingListUpserted extends HouseholdEvent {
  const ShoppingListUpserted(this.list);
  final ShoppingList list;
}

class ShoppingListDeleted extends HouseholdEvent {
  const ShoppingListDeleted(this.listId);
  final String listId;
}

class ShoppingItemUpserted extends HouseholdEvent {
  const ShoppingItemUpserted(this.item);
  final ShoppingItem item;
}

class ShoppingItemDeleted extends HouseholdEvent {
  const ShoppingItemDeleted(this.listId, this.itemId);
  final String listId;
  final String itemId;
}

class ChoreUpserted extends HouseholdEvent {
  const ChoreUpserted(this.chore);
  final Chore chore;
}

class ChoreDeleted extends HouseholdEvent {
  const ChoreDeleted(this.choreId);
  final String choreId;
}

class CalendarEventUpserted extends HouseholdEvent {
  const CalendarEventUpserted(this.event);
  final CalendarEvent event;
}

class CalendarEventDeleted extends HouseholdEvent {
  const CalendarEventDeleted(this.eventId);
  final String eventId;
}

/// Um aviso pessoal novo (chega pelo canal da pessoa, seja qual for a casa aberta).
class NotificationReceived extends HouseholdEvent {
  const NotificationReceived(this.notification);
  final AppNotification notification;
}

/// Mudou alguma coisa nas recompensas ou nos pedidos (o ecrã volta a ler tudo, porque os saldos mudam).
class RewardsChanged extends HouseholdEvent {
  const RewardsChanged();
}

/// Mudou a mesada de alguém (valores, ou um período pago).
class AllowanceChanged extends HouseholdEvent {
  const AllowanceChanged(this.userId);
  final String userId;
}

/// Mudou quem está numa casa: alguém entrou ou mudou de papel ([member]), ou saiu ([removedUserId]). A saída também
/// chega pelo canal pessoal de quem saiu.
class MembersChanged extends HouseholdEvent {
  const MembersChanged(this.householdId, {this.member, this.removedUserId});
  final String householdId;
  final Member? member;
  final String? removedUserId;
}

/// O administrador mudou o que cada papel pode fazer: a app volta a ler o acesso às áreas.
class PermissionsChanged extends HouseholdEvent {
  const PermissionsChanged(this.householdId);
  final String householdId;
}

/// Alguém marcou ou cancelou uma ausência (o ecrã dos membros volta a ler as ausências).
class AbsencesChanged extends HouseholdEvent {
  const AbsencesChanged();
}

/// Mudou uma ligação deste membro ao Google Calendar ou ao Outlook (ligada, sincronizada, desligada).
/// Chega pelo canal pessoal; o ecrã das ligações volta a ler tudo.
class CalendarConnectionsChanged extends HouseholdEvent {
  const CalendarConnectionsChanged();
}

/// Ligação SignalR ao agregado aberto. Volta a ligar-se sozinha quando a rede falha
/// e avisa (via [reconnected]) para os ecrãs recarregarem o que possam ter perdido.
class HouseholdRealtime {
  HouseholdRealtime({required this.baseUrl, required this.accessToken});

  final String baseUrl;

  /// O token para ligar (o SignalR só o envia ao ligar e ao voltar a ligar); pode renovar a sessão antes.
  final FutureOr<String> Function() accessToken;

  final _events = StreamController<HouseholdEvent>.broadcast();
  final _reconnected = StreamController<void>.broadcast();
  HubConnection? _connection;
  String? _householdId;

  Stream<HouseholdEvent> get events => _events.stream;
  Stream<void> get reconnected => _reconnected.stream;

  Future<void> connect(String householdId) async {
    if (_householdId == householdId && _connection?.state == HubConnectionState.Connected) return;
    await disconnect();
    _householdId = householdId;

    final connection = HubConnectionBuilder()
        .withUrl(
          '$baseUrl/hubs/household',
          options: HttpConnectionOptions(accessTokenFactory: () async => await accessToken()),
        )
        .withAutomaticReconnect(retryDelays: [0, 2000, 5000, 10000, 30000])
        .build();

    connection.on(
      'ShoppingListUpserted',
      (args) => _emit(args, (a) => ShoppingListUpserted(ShoppingList.fromJson(_map(a[0])))),
    );
    connection.on('ShoppingListDeleted', (args) => _emit(args, (a) => ShoppingListDeleted(a[0] as String)));
    connection.on(
      'ShoppingItemUpserted',
      (args) => _emit(args, (a) => ShoppingItemUpserted(ShoppingItem.fromJson(_map(a[0])))),
    );
    connection.on(
      'ShoppingItemDeleted',
      (args) => _emit(args, (a) => ShoppingItemDeleted(a[0] as String, a[1] as String)),
    );

    connection.on('ChoreUpserted', (args) => _emit(args, (a) => ChoreUpserted(Chore.fromJson(_map(a[0])))));
    connection.on('ChoreDeleted', (args) => _emit(args, (a) => ChoreDeleted(a[0] as String)));

    connection.on(
      'CalendarEventUpserted',
      (args) => _emit(args, (a) => CalendarEventUpserted(CalendarEvent.fromJson(_map(a[0])))),
    );
    connection.on('CalendarEventDeleted', (args) => _emit(args, (a) => CalendarEventDeleted(a[0] as String)));

    connection.on(
      'NotificationCreated',
      (args) => _emit(args, (a) => NotificationReceived(AppNotification.fromJson(_map(a[0])))),
    );

    for (final name in ['RewardUpserted', 'RewardDeleted', 'RedemptionUpserted', 'RedemptionDeleted']) {
      connection.on(name, (args) => _emit(args, (_) => const RewardsChanged()));
    }

    connection.on(
      'MemberUpserted',
      (args) => _emit(args, (a) => MembersChanged(a[0] as String, member: Member.fromJson(_map(a[1])))),
    );
    connection.on(
      'MemberDeleted',
      (args) => _emit(args, (a) => MembersChanged(a[0] as String, removedUserId: a[1] as String)),
    );

    connection.on('AllowanceChanged', (args) => _emit(args, (a) => AllowanceChanged(a[0] as String)));

    connection.on('PermissionsChanged', (args) => _emit(args, (a) => PermissionsChanged(a[0] as String)));

    for (final name in ['AbsenceUpserted', 'AbsenceDeleted']) {
      connection.on(name, (args) => _emit(args, (_) => const AbsencesChanged()));
    }

    for (final name in ['CalendarConnectionUpserted', 'CalendarConnectionDeleted']) {
      connection.on(name, (args) => _emit(args, (_) => const CalendarConnectionsChanged()));
    }

    connection.onreconnected(({connectionId}) async {
      await _join(connection, householdId);
      _reconnected.add(null);
    });

    _connection = connection;
    try {
      await connection.start();
      await _join(connection, householdId);
    } catch (e) {
      // A app continua a funcionar sem tempo real; os dados recarregam ao abrir cada ecrã.
      debugPrint('Kubico: tempo real indisponível ($e)');
    }
  }

  Future<void> disconnect() async {
    final connection = _connection;
    _connection = null;
    _householdId = null;
    if (connection != null) {
      try {
        await connection.stop();
      } catch (_) {}
    }
  }

  Future<void> dispose() async {
    await disconnect();
    await _events.close();
    await _reconnected.close();
  }

  /// Para os testes: injeta um evento como se tivesse chegado do servidor.
  @visibleForTesting
  void emitForTest(HouseholdEvent event) => _events.add(event);

  Future<void> _join(HubConnection connection, String householdId) =>
      connection.invoke('JoinHousehold', args: [householdId]);

  void _emit(List<Object?>? args, HouseholdEvent Function(List<Object?> a) build) {
    if (args == null || args.isEmpty || _events.isClosed) return;
    try {
      _events.add(build(args));
    } catch (e) {
      debugPrint('Kubico: evento inválido ($e)');
    }
  }

  static Map<String, dynamic> _map(Object? value) => Map<String, dynamic>.from(value as Map);
}
