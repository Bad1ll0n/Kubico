import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../api/models.dart';
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
        await signOut();
      } else {
        // Sem rede: mantém o ecrã de entrada em vez de ficar preso a carregar.
        _setSignedOut();
      }
    }
  }

  Future<void> signIn(String email, String password) async => _startSession(await api.login(email.trim(), password));

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

  /// Termina a sessão em todos os dispositivos (por exemplo, depois de perder o telemóvel).
  Future<void> signOutEverywhere() async {
    await api.logoutEverywhere();
    await _forgetSession();
  }

  /// Troca o refresh token guardado por um par novo. Só há um pedido de cada vez: vários 401 ao mesmo tempo
  /// esperam pelo mesmo. Devolve false se não houver sessão para renovar (ou se a rede falhar).
  Future<bool> renewSession() => _renewing ??= _renew().whenComplete(() => _renewing = null);

  /// Para o tempo real (o SignalR só envia o token ao ligar): um token de acesso que dure pelo menos mais um minuto.
  Future<String> freshAccessToken() async {
    final expiresAt = _accessExpiresAt;
    if (api.accessToken != null && (expiresAt == null || expiresAt.isBefore(DateTime.now().add(_margin)))) {
      await renewSession();
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
      if (e.isUnauthorized) await _forgetSession();
      return false;
    }
  }

  Future<void> _forgetSession() async {
    api.accessToken = null;
    _accessExpiresAt = null;
    await _store.write(_tokenKey, null);
    await _store.write(_refreshKey, null);
    _setSignedOut();
  }

  /// Volta a pedir o utilizador e os agregados ao servidor.
  Future<void> refresh() async {
    final me = await api.getMe();
    user = me.user;
    households = me.households;
    isSupport = me.isSupport;
    status = SessionStatus.signedIn;
    notifyListeners();
  }

  Future<void> selectHousehold(String householdId) async {
    _selectedHouseholdId = householdId;
    await _store.write(_householdKey, householdId);
    notifyListeners();
  }

  Future<void> createHousehold(String name) async {
    final created = await api.createHousehold(name.trim());
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
