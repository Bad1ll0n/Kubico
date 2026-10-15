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

/// Ligação SignalR ao agregado aberto. Volta a ligar-se sozinha quando a rede falha
/// e avisa (via [reconnected]) para os ecrãs recarregarem o que possam ter perdido.
class HouseholdRealtime {
  HouseholdRealtime({required this.baseUrl, required this.accessToken});

  final String baseUrl;
  final String Function() accessToken;

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
          options: HttpConnectionOptions(accessTokenFactory: () async => accessToken()),
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
