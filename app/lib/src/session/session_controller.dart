import 'dart:async';
import 'dart:convert';

import 'package:clock/clock.dart';
import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import 'token_store.dart';

enum SessionStatus { loading, signedOut, signedIn }

/// Estado global da sessão: quem está autenticado, os seus agregados e qual está aberto.
class SessionController extends ChangeNotifier {
  // ignore: prefer_initializing_formals
  SessionController({required this.api, required TokenStore store}) : _store = store {
    api.onUnauthorized = renewSession;
  }

  static const _tokenKey = 'kubico.accessToken';
  static const _refreshKey = 'kubico.refreshToken';
  static const _householdKey = 'kubico.householdId';

  /// A última resposta de /api/me (quem é e as casas; sem segredos): abre a app sem rede.
  static const _meKey = 'kubico.me';

  final ApiClient api;
  final TokenStore _store;

  SessionStatus status = SessionStatus.loading;
  User? user;
  List<HouseholdSummary> households = const [];

  /// Esta conta mantém o Kubico (vê as mensagens enviadas da ajuda).
  bool isSupport = false;

  /// Um convite que chegou por link (?convite=…) e espera que a pessoa entre ou crie conta.
  String? pendingInvite;
  String? _selectedHouseholdId;
  DateTime? _accessExpiresAt;
  Future<bool>? _renewing;
  Timer? _retry;
  var _retryDelay = const Duration(seconds: 5);

  /// A sessão abriu com a cópia guardada, porque o servidor não respondeu (sem rede ou em baixo); volta a tentar
  /// sozinha ([retryIfOffline]).
  bool isOffline = false;

  /// Chamado sempre que a sessão deste aparelho acaba (terminou aqui, noutro aparelho, expirou ou a conta foi
  /// apagada): apaga o que a app guardou (a cópia das listas, a fila, os avisos) e desliga o tempo real.
  Future<void> Function()? onSessionEnded;

  HouseholdSummary? get currentHousehold {
    if (households.isEmpty) return null;
    return households.firstWhere((h) => h.id == _selectedHouseholdId, orElse: () => households.first);
  }

  /// Retoma a sessão guardada, se existir e ainda for válida.
  Future<void> restore() async {
    final token = await _store.read(_tokenKey);
    _selectedHouseholdId = await _store.read(_householdKey);
    if (token == null) {
      _setSignedOut();
      return;
    }

    api.accessToken = token;
    try {
      await refresh();
    } on ApiException catch (e) {
      if (e.isUnauthorized) {
        // Só um 401 do próprio servidor termina a sessão (uma falha de rede ao renovar não chega aqui como 401).
        await signOut();
        return;
      }
      // Sem rede ou o servidor em baixo: abre com a última cópia (a lista de compras sem rede funciona) e volta a
      // tentar sozinha. Sem cópia, fica o ecrã de entrada.
      final cached = await _store.read(_meKey);
      if (cached == null) {
        _setSignedOut();
        return;
      }
      _apply(Me.fromJson(jsonDecode(cached) as Map<String, dynamic>));
      isOffline = true;
      notifyListeners();
      _scheduleRetry();
    }
  }

  /// Volta a falar com o servidor se a sessão abriu sem ele (ao voltar à app, por exemplo).
  Future<void> retryIfOffline() async {
    if (!isOffline) return;
    _retry?.cancel();
    _retry = null;
    try {
      await refresh();
      _retryDelay = const Duration(seconds: 5);
    } on ApiException catch (e) {
      if (e.isUnauthorized) {
        await signOut();
      } else {
        _scheduleRetry();
      }
    }
  }

  void _scheduleRetry() {
    _retry ??= Timer(_retryDelay, () {
      _retry = null;
      unawaited(retryIfOffline());
    });
    // 5 s, 10 s, 20 s… até 2 minutos.
    final next = _retryDelay * 2;
    _retryDelay = next > const Duration(minutes: 2) ? const Duration(minutes: 2) : next;
  }

  void _apply(Me me) {
    user = me.user;
    households = me.households;
    isSupport = me.isSupport;
    status = SessionStatus.signedIn;
  }

  @override
  void dispose() {
    _retry?.cancel();
    super.dispose();
  }

  /// Com verificação em dois passos, lança [TwoFactorRequired]; depois entra-se com [signInWithCode].
  Future<void> signIn(String email, String password) async => _startSession(await api.login(email.trim(), password));

  Future<void> signInWithCode(String challengeToken, String code) async =>
      _startSession(await api.loginWithCode(challengeToken, code.trim()));

  Future<void> register(String email, String password, String displayName) async =>
      _startSession(await api.register(email.trim(), password, displayName.trim()));

  /// Entrada de uma criança: código da família, o seu perfil e o PIN.
  Future<void> signInChild(String familyCode, String userId, String pin) async =>
      _startSession(await api.childLogin(familyCode.trim(), userId, pin));

  /// Sai da casa e abre outra (ou o ecrã de boas-vindas, se não sobrar nenhuma).
  Future<void> leaveHousehold(String householdId) async {
    await api.leaveHousehold(householdId);
    await householdLost(householdId);
  }

  /// Alguém mudou numa casa. Se foi esta pessoa a sair (ou a ser tirada), abre-se outra casa; se mudou o seu papel,
  /// voltam a ler-se as casas, para as permissões da app acompanharem.
  Future<void> memberChanged(String householdId, {Member? member, String? removedUserId}) async {
    final me = user?.id;
    if (me == null) return;
    if (removedUserId == me) {
      await householdLost(householdId);
      return;
    }

    final known = households.where((h) => h.id == householdId).firstOrNull;
    if (member != null && member.userId == me && known != null && known.role != member.role) {
      await refresh();
    }
  }

  /// Esta pessoa deixou de estar na casa (saiu ou foi tirada): volta a ler as casas. Um perfil de criança apagado
  /// já não tem sessão, e volta ao ecrã de entrada.
  Future<void> householdLost(String householdId) async {
    if (_selectedHouseholdId == householdId) {
      _selectedHouseholdId = null;
      await _store.write(_householdKey, null);
    }
    try {
      await refresh();
    } on ApiException catch (e) {
      if (e.isUnauthorized) await _forgetSession();
    }
  }

  /// Termina a sessão neste dispositivo. Sem rede, termina só aqui (o refresh token expira sozinho).
  Future<void> signOut() async {
    final refreshToken = await _store.read(_refreshKey);
    if (refreshToken != null) {
      try {
        await api.logout(refreshToken);
      } on ApiException {
        // Sem rede ou sessão já terminada: não impede de sair.
      }
    }
    await _forgetSession();
  }

  /// A conta foi apagada: esquece a sessão neste dispositivo (no servidor já não existe).
  Future<void> forgetDeletedAccount() => _forgetSession();

  /// Termina a sessão em todos os dispositivos (por exemplo, depois de perder o telemóvel). Os endereços de
  /// calendário subscritos também deixam de servir, a não ser com [resetCalendarFeeds] a false.
  Future<void> signOutEverywhere({bool resetCalendarFeeds = true}) async {
    await api.logoutEverywhere(resetCalendarFeeds: resetCalendarFeeds);
    await _forgetSession();
  }

  /// Troca o refresh token guardado por um par novo. Só há um pedido de cada vez: vários 401 ao mesmo tempo
  /// esperam pelo mesmo. Devolve false se não houver sessão para renovar (ou se o servidor a terminou); sem rede, lança
  /// o erro de ligação.
  Future<bool> renewSession() => _renewing ??= _renew().whenComplete(() => _renewing = null);

  /// Para o tempo real (o SignalR só envia o token ao ligar): um token de acesso que dure pelo menos mais um minuto.
  Future<String> freshAccessToken() async {
    final expiresAt = _accessExpiresAt;
    if (api.accessToken != null && (expiresAt == null || expiresAt.isBefore(clock.now().add(_margin)))) {
      try {
        await renewSession();
      } on ApiException {
        // Sem rede: a ligação vai falhar e voltar a tentar mais tarde.
      }
    }
    return api.accessToken ?? '';
  }

  static const _margin = Duration(minutes: 1);

  Future<bool> _renew() async {
    final refreshToken = await _store.read(_refreshKey);
    if (refreshToken == null) return false;
    try {
      await _saveTokens(await api.refreshSession(refreshToken));
      return true;
    } on ApiException catch (e) {
      // O servidor terminou a sessão (expirou, ou terminou-se em todos os dispositivos).
      if (e.isUnauthorized) {
        await _forgetSession();
        return false;
      }
      // Sem rede ou o servidor em baixo: não é o fim da sessão. O erro segue para quem fez o pedido (como "sem
      // ligação"), em vez de um 401 que a terminava e apagava a fila das compras.
      rethrow;
    }
  }

  Future<void> _forgetSession() async {
    final hadSession = api.accessToken != null;
    api.accessToken = null;
    _accessExpiresAt = null;
    await _store.write(_tokenKey, null);
    await _store.write(_refreshKey, null);
    await _store.write(_meKey, null);
    _retry?.cancel();
    _retry = null;
    isOffline = false;
    if (hadSession) {
      try {
        await onSessionEnded?.call();
      } catch (e) {
        debugPrint('Kubico: não foi possível apagar os dados locais ($e)');
      }
    }
    _setSignedOut();
  }

  /// Volta a pedir o utilizador e os agregados ao servidor.
  Future<void> refresh() async {
    final me = await api.getMe();
    _apply(me);
    isOffline = false;
    notifyListeners();
    await _store.write(_meKey, jsonEncode(me.toJson()));
    // Os avisos chegam na língua da conta: acerta-a com a que a app está a usar.
    if (me.user.language != AppLanguage.code) unawaited(_saveLanguage(AppLanguage.code));
  }

  /// Muda o meu nome e volta a ler as casas (onde o nome aparece).
  Future<void> changeName(String name) async {
    user = await api.changeName(name);
    notifyListeners();
    await refresh();
  }

  /// Muda a língua da app (fica guardada no dispositivo) e a da conta, para os avisos.
  Future<void> setLanguage(String language) async {
    await AppLanguage.set(language);
    if (user != null) await _saveLanguage(AppLanguage.code);
  }

  Future<void> _saveLanguage(String language) async {
    try {
      user = await api.setLanguage(language);
      notifyListeners();
    } catch (_) {
      // Sem rede: fica para a próxima vez que a sessão abrir.
    }
  }

  Future<void> selectHousehold(String householdId) async {
    _selectedHouseholdId = householdId;
    await _store.write(_householdKey, householdId);
    notifyListeners();
  }

  Future<void> createHousehold(String name, {String? template}) async {
    final created = await api.createHousehold(name.trim(), template: template);
    await refresh();
    await selectHousehold(created.id);
  }

  Future<void> joinHousehold(String code) async {
    final joined = await api.acceptInvite(code);
    pendingInvite = null;
    await refresh();
    await selectHousehold(joined.id);
  }

  Future<void> _startSession(AuthResult result) async {
    await _saveTokens(result);
    await refresh();
  }

  Future<void> _saveTokens(AuthResult result) async {
    api.accessToken = result.accessToken;
    _accessExpiresAt = result.expiresAt;
    await _store.write(_tokenKey, result.accessToken);
    await _store.write(_refreshKey, result.refreshToken);
  }

  void _setSignedOut() {
    user = null;
    households = const [];
    status = SessionStatus.signedOut;
    notifyListeners();
  }
}
