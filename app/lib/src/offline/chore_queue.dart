import 'dart:async';
import 'dart:convert';

import 'package:clock/clock.dart';
import 'package:flutter/widgets.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../storage/key_value_store.dart';

/// Uma tarefa concluída sem rede, à espera de ir para o servidor.
@immutable
class PendingCompletion {
  const PendingCompletion({
    required this.householdId,
    required this.choreId,
    required this.title,
    required this.doneByUserId,
    required this.queuedAt,
  });

  final String householdId;
  final String choreId;
  final String title;
  final String? doneByUserId;
  final DateTime queuedAt;

  Map<String, dynamic> toJson() => {
    'householdId': householdId,
    'choreId': choreId,
    'title': title,
    'doneByUserId': doneByUserId,
    'queuedAt': queuedAt.toUtc().toIso8601String(),
  };

  factory PendingCompletion.fromJson(Map<String, dynamic> json) => PendingCompletion(
    householdId: json['householdId'] as String,
    choreId: json['choreId'] as String,
    title: json['title'] as String,
    doneByUserId: json['doneByUserId'] as String?,
    queuedAt: DateTime.parse(json['queuedAt'] as String),
  );
}

/// Uma conclusão que o servidor recusou de vez (por exemplo, alguém já a fez: 409), para a app avisar.
@immutable
class CompletionRejection {
  const CompletionRejection(this.completion, this.message);

  final PendingCompletion completion;
  final String message;
}

/// Concluir tarefas sem rede (F4-14), com o mesmo modelo da fila das compras: a conclusão fica guardada no aparelho
/// (logo, não pode perder-se), envia-se quando a rede volta e com a sessão pronta, repete as falhas que passam
/// (sem rede, 401, 408, 429, 5xx; um 5xx [maxServerFailures] vezes seguidas solta-a) e anuncia em [rejections] o que
/// o servidor recusa de vez. As tarefas com foto de prova não entram na fila (a foto tem de ir primeiro).
class ChoreQueue extends ChangeNotifier {
  ChoreQueue({required this.api, required KeyValueStore store}) : _store = store; // ignore: prefer_initializing_formals

  static const _key = 'kubico.offline.choreCompletions';
  static const maxServerFailures = 10;

  final ApiClient api;
  final KeyValueStore _store;
  final _pending = <PendingCompletion>[];
  final _rejections = StreamController<CompletionRejection>.broadcast();
  final _sent = StreamController<PendingCompletion>.broadcast();
  Future<void>? _syncing;
  var _serverFailures = 0;

  /// Recusadas de vez pelo servidor.
  Stream<CompletionRejection> get rejections => _rejections.stream;

  /// Enviadas com êxito (para recarregar os pontos e as tarefas).
  Stream<PendingCompletion> get sent => _sent.stream;

  List<PendingCompletion> get pending => List.unmodifiable(_pending);

  /// As tarefas desta casa concluídas aqui e ainda por enviar.
  Set<String> pendingIn(String householdId) => {
    for (final p in _pending)
      if (p.householdId == householdId) p.choreId,
  };

  Future<void> restore() async {
    try {
      final saved = await _store.read(_key);
      if (saved == null) return;
      _pending
        ..clear()
        ..addAll([for (final p in jsonDecode(saved) as List) PendingCompletion.fromJson(p as Map<String, dynamic>)]);
      notifyListeners();
    } catch (e) {
      debugPrint('Kubico: a fila das tarefas não se conseguiu ler ($e)');
    }
  }

  /// Guarda a conclusão (já gravada no aparelho quando o Future acaba).
  Future<void> enqueue({
    required String householdId,
    required String choreId,
    required String title,
    String? doneByUserId,
  }) async {
    if (_pending.any((p) => p.choreId == choreId)) return;
    _pending.add(
      PendingCompletion(
        householdId: householdId,
        choreId: choreId,
        title: title,
        doneByUserId: doneByUserId,
        queuedAt: clock.now(),
      ),
    );
    await _save();
    notifyListeners();
  }

  /// Envia o que está na fila, por ordem. Só um envio de cada vez.
  Future<void> sync() => _syncing ??= _flush().whenComplete(() => _syncing = null);

  Future<void> _flush() async {
    if (api.accessToken == null) return;
    while (_pending.isNotEmpty) {
      final next = _pending.first;
      try {
        await api.completeChore(next.householdId, next.choreId, doneByUserId: next.doneByUserId);
        _serverFailures = 0;
        _pending.removeAt(0);
        await _save();
        notifyListeners();
        _sent.add(next);
      } on ApiException catch (e) {
        if (_isTransient(e)) {
          if (e.statusCode >= 500 && ++_serverFailures < maxServerFailures) return;
          if (e.statusCode < 500) return;
        }
        // Recusada de vez (ou o servidor falhou demasiadas vezes): sai da fila e avisa.
        _serverFailures = 0;
        _pending.removeAt(0);
        await _save();
        notifyListeners();
        _rejections.add(CompletionRejection(next, e.message));
      }
    }
  }

  static bool _isTransient(ApiException e) =>
      e.statusCode == 0 || e.statusCode == 401 || e.statusCode == 408 || e.statusCode == 429 || e.statusCode >= 500;

  Future<void> _save() => _store.write(_key, jsonEncode([for (final p in _pending) p.toJson()]));

  /// A sessão acabou: a fila era desta pessoa.
  Future<void> clear() async {
    _pending.clear();
    _serverFailures = 0;
    await _store.write(_key, null);
    notifyListeners();
  }

  @override
  void dispose() {
    unawaited(_rejections.close());
    unawaited(_sent.close());
    super.dispose();
  }
}

/// A fila, se a app a tiver (alguns testes montam os ecrãs sem ela).
ChoreQueue? maybeChoreQueue(BuildContext context, {bool listen = true}) {
  try {
    return Provider.of<ChoreQueue>(context, listen: listen);
  } on ProviderNotFoundException {
    return null;
  }
}
