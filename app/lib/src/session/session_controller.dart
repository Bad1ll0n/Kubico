import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import 'token_store.dart';

enum SessionStatus { loading, signedOut, signedIn }

/// Estado global da sessão: quem está autenticado, os seus agregados e qual está aberto.
class SessionController extends ChangeNotifier {
  SessionController({required this.api, required TokenStore store})
    : _store = store; // ignore: prefer_initializing_formals

  static const _tokenKey = 'kubico.accessToken';
  static const _householdKey = 'kubico.householdId';

  final ApiClient api;
  final TokenStore _store;

  SessionStatus status = SessionStatus.loading;
  User? user;
  List<HouseholdSummary> households = const [];
  String? _selectedHouseholdId;

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

  Future<void> signOut() async {
    api.accessToken = null;
    await _store.write(_tokenKey, null);
    _setSignedOut();
  }

  /// Volta a pedir o utilizador e os agregados ao servidor.
  Future<void> refresh() async {
    final me = await api.getMe();
    user = me.user;
    households = me.households;
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
    await refresh();
    await selectHousehold(joined.id);
  }

  Future<void> _startSession(AuthResult result) async {
    api.accessToken = result.accessToken;
    await _store.write(_tokenKey, result.accessToken);
    await refresh();
  }

  void _setSignedOut() {
    user = null;
    households = const [];
    status = SessionStatus.signedOut;
    notifyListeners();
  }
}
