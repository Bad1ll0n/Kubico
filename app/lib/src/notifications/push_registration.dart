import 'dart:async';

import 'package:flutter/foundation.dart';

import '../api/api_client.dart';

/// De onde vem o identificador push deste dispositivo (no Firebase Cloud Messaging, o FID).
abstract class PushTokenSource {
  /// android, ios ou web; null quando este dispositivo não recebe avisos fora da app (Windows, Linux).
  String? get platform;

  /// O token atual, pedindo autorização ao utilizador se ainda não a deu. Null se recusar ou não houver push.
  Future<String?> getToken();

  /// O Firebase renova o token de vez em quando; o novo tem de chegar ao servidor.
  Stream<String> get onTokenRefresh;

  /// Ao terminar a sessão: o token antigo deixa de servir.
  Future<void> deleteToken();
}

/// Sem Firebase configurado (ou num dispositivo sem push): os avisos ficam só no sino da app.
class NoPushTokenSource implements PushTokenSource {
  const NoPushTokenSource();

  @override
  String? get platform => null;

  @override
  Future<String?> getToken() async => null;

  @override
  Stream<String> get onTokenRefresh => const Stream.empty();

  @override
  Future<void> deleteToken() async {}
}

/// Liga o telemóvel à conta para receber avisos fora da app: regista o token ao entrar (e sempre que o Firebase o
/// renova) e apaga-o ao terminar a sessão, para os avisos não irem parar ao telemóvel de outra pessoa.
class PushRegistration {
  /// [source] pode chegar mais tarde (o Firebase arranca em paralelo com a app).
  // ignore: prefer_initializing_formals
  PushRegistration({required this.api, required FutureOr<PushTokenSource> source}) : _source = source;

  final ApiClient api;
  final FutureOr<PushTokenSource> _source;

  String? _token;
  StreamSubscription<String>? _refreshes;

  /// Depois de entrar. Falhas (sem rede, autorização recusada) não impedem de usar a app.
  Future<void> register() async {
    final source = await _source;
    final platform = source.platform;
    if (platform == null) return;
    try {
      final token = await source.getToken();
      if (token == null) return;
      await api.registerPushDevice(token, platform);
      _token = token;
      _refreshes ??= source.onTokenRefresh.listen((fresh) async {
        _token = fresh;
        try {
          await api.registerPushDevice(fresh, platform);
        } on ApiException catch (e) {
          debugPrint('Kubico: não foi possível registar o novo token push (${e.message})');
        }
      });
    } catch (e) {
      debugPrint('Kubico: avisos no telemóvel indisponíveis ($e)');
    }
  }

  /// Antes de terminar a sessão (ainda com sessão, para o pedido ser aceite).
  Future<void> unregister() async {
    final source = await _source;
    await _refreshes?.cancel();
    _refreshes = null;
    final token = _token;
    _token = null;
    if (token != null) {
      try {
        await api.unregisterPushDevice(token);
      } on ApiException {
        // Sem rede: o servidor esquece o token quando o Firebase disser que já não existe.
      }
    }
    try {
      await source.deleteToken();
    } catch (e) {
      debugPrint('Kubico: não foi possível apagar o token push ($e)');
    }
  }
}
