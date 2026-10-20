import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;

import 'models.dart';

/// Um ficheiro a enviar num pedido multipart.
typedef _Upload = ({String field, List<int> bytes, String filename});

/// A palavra-passe está certa mas a conta tem verificação em dois passos: falta o código, que se envia com
/// [challengeToken] (vale 5 minutos) em [ApiClient.loginWithCode].
class TwoFactorRequired implements Exception {
  const TwoFactorRequired(this.challengeToken);

  final String challengeToken;

  @override
  String toString() => 'Falta o código da verificação em dois passos.';
}

/// Erro devolvido pela API, já com uma mensagem pronta a mostrar ao utilizador.
class ApiException implements Exception {
  const ApiException(this.statusCode, this.message);

  final int statusCode;
  final String message;

  bool get isUnauthorized => statusCode == 401;

  @override
  String toString() => message;
}

class ApiClient {
  ApiClient({required this.baseUrl, http.Client? httpClient, this.deviceKind}) : _http = httpClient ?? http.Client();

  final String baseUrl;
  final http.Client _http;

  /// Em que tipo de dispositivo corre a app (android, ios, windows, macos, linux, web), para a lista de sessões.
  final String? deviceKind;

  /// Token JWT da sessão atual; null quando ninguém tem sessão iniciada.
  String? accessToken;

  /// Chamado quando um pedido com sessão recebe 401 (o token de acesso expirou): renova a sessão e, se conseguir,
  /// o pedido repete-se uma vez. Quem o define é o SessionController.
  Future<bool> Function()? onUnauthorized;

  /// Pedidos de autenticação: um 401 aqui não se resolve renovando a sessão.
  static const _authPaths = {
    '/api/auth/login/2fa',
    '/api/auth/login',
    '/api/auth/register',
    '/api/auth/refresh',
    '/api/auth/logout',
    '/api/auth/child/profiles',
    '/api/auth/child/login',
  };

  // --- Autenticação ---

  Future<AuthResult> register(String email, String password, String displayName) async => AuthResult.fromJson(
    await _send('POST', '/api/auth/register', body: {'email': email, 'password': password, 'displayName': displayName}),
  );

  /// Entra com email e palavra-passe. Com verificação em dois passos, lança [TwoFactorRequired].
  Future<AuthResult> login(String email, String password) async {
    final json =
        await _send('POST', '/api/auth/login', body: {'email': email, 'password': password}) as Map<String, dynamic>;
    if (json['twoFactorRequired'] == true) throw TwoFactorRequired(json['challengeToken'] as String);
    return AuthResult.fromJson(json);
  }

  /// O segundo passo da entrada: o código da app de autenticação (ou um de recuperação).
  Future<AuthResult> loginWithCode(String challengeToken, String code) async => AuthResult.fromJson(
    await _send('POST', '/api/auth/login/2fa', body: {'challengeToken': challengeToken, 'code': code}),
  );

  Future<({bool enabled, int recoveryCodesLeft})> getTwoFactor() async {
    final json = await _send('GET', '/api/account/2fa') as Map<String, dynamic>;
    return (enabled: json['enabled'] as bool, recoveryCodesLeft: json['recoveryCodesLeft'] as int);
  }

  /// Começa a ligar: o segredo (para escrever à mão) e o endereço para o código QR.
  Future<(String, String)> setUpTwoFactor() async {
    final json = await _send('POST', '/api/account/2fa/setup') as Map<String, dynamic>;
    return (json['secret'] as String, json['uri'] as String);
  }

  /// Confirma com um código da app; devolve os códigos de recuperação (só se mostram desta vez).
  Future<List<String>> enableTwoFactor(String code) async {
    final json = await _send('POST', '/api/account/2fa/enable', body: {'code': code}) as Map<String, dynamic>;
    return (json['recoveryCodes'] as List).cast<String>();
  }

  Future<void> disableTwoFactor(String password, String code) =>
      _send('POST', '/api/account/2fa/disable', body: {'password': password, 'code': code});

  /// Troca o refresh token por um par novo (o antigo deixa de servir).
  Future<AuthResult> refreshSession(String refreshToken) async =>
      AuthResult.fromJson(await _send('POST', '/api/auth/refresh', body: {'refreshToken': refreshToken}));

  /// Termina a sessão deste dispositivo.
  Future<void> logout(String refreshToken) => _send('POST', '/api/auth/logout', body: {'refreshToken': refreshToken});

  /// Termina a sessão em todos os dispositivos.
  Future<void> logoutEverywhere() => _send('POST', '/api/auth/logout-all');

  /// Os dispositivos com sessão iniciada (o deste vem marcado).
  Future<List<DeviceSession>> getSessions() async => [
    for (final s in await _send('GET', '/api/auth/sessions') as List) DeviceSession.fromJson(s as Map<String, dynamic>),
  ];

  /// Termina a sessão noutro dispositivo.
  Future<void> endSession(String sessionId) => _send('DELETE', '/api/auth/sessions/$sessionId');

  Future<Me> getMe() async => Me.fromJson(await _send('GET', '/api/me'));

  /// Tudo o que o Kubico guarda sobre esta conta, num ficheiro JSON (nome sugerido e conteúdo).
  Future<(String, Uint8List)> exportAccount() async {
    final response = await _execute('GET', '/api/account/export');
    final disposition = response.headers['content-disposition'] ?? '';
    final name = RegExp(r'filename="?([^";]+)"?').firstMatch(disposition)?.group(1) ?? 'kubico-dados.json';
    return (name, response.bodyBytes);
  }

  /// Apaga a conta (confirma com a palavra-passe).
  Future<void> deleteAccount(String password) => _send('DELETE', '/api/account', body: {'password': password});

  // --- Agregados ---

  /// Cria a casa; com [template], já vem com as listas, tarefas e recompensas desse modelo.
  Future<HouseholdSummary> createHousehold(String name, {String? template}) async =>
      HouseholdSummary.fromJson(await _send('POST', '/api/households', body: {'name': name, 'template': ?template}));

  Future<List<HouseholdTemplate>> getHouseholdTemplates() async => [
    for (final t in await _send('GET', '/api/household-templates') as List)
      HouseholdTemplate.fromJson(t as Map<String, dynamic>),
  ];

  Future<HouseholdDetails> getHousehold(String householdId) async =>
      HouseholdDetails.fromJson(await _send('GET', '/api/households/$householdId'));

  Future<Invite> createInvite(String householdId, HouseholdRole role) async =>
      Invite.fromJson(await _send('POST', '/api/households/$householdId/invites', body: {'role': role.apiValue}));

  Future<InvitePreview> previewInvite(String code) async => InvitePreview.fromJson(
    await _send('GET', '/api/invites/${Uri.encodeComponent(code.trim())}') as Map<String, dynamic>,
  );

  Future<HouseholdSummary> acceptInvite(String code) async =>
      HouseholdSummary.fromJson(await _send('POST', '/api/invites/${Uri.encodeComponent(code.trim())}/accept'));

  // --- Ajuda ---

  /// "Reportar um problema" ou "Sugerir uma ideia".
  Future<void> sendFeedback(FeedbackKind kind, String message, {String? householdId}) =>
      _send('POST', '/api/feedback', body: {'kind': kind.apiValue, 'message': message, 'householdId': ?householdId});

  /// As mensagens recebidas (só para quem mantém o Kubico).
  Future<List<FeedbackItem>> getFeedback() async => [
    for (final f in await _send('GET', '/api/feedback') as List) FeedbackItem.fromJson(f as Map<String, dynamic>),
  ];

  // --- Membros e perfis de criança ---

  /// As ausências que ainda não acabaram.
  Future<HouseholdPermissions> getPermissions(String householdId) async =>
      HouseholdPermissions.fromJson(await _send('GET', '/api/households/$householdId/permissions'));

  Future<HouseholdPermissions> savePermissions(String householdId, List<RolePermissions> roles) async =>
      HouseholdPermissions.fromJson(
        await _send(
          'PUT',
          '/api/households/$householdId/permissions',
          body: {
            'roles': [for (final r in roles) r.toJson()],
          },
        ),
      );

  Future<List<MemberAbsence>> getAbsences(String householdId) async => [
    for (final a in await _send('GET', '/api/households/$householdId/absences') as List)
      MemberAbsence.fromJson(a as Map<String, dynamic>),
  ];

  Future<MemberAbsence> createAbsence(
    String householdId, {
    required String userId,
    required DateTime start,
    required DateTime end,
    String? reason,
  }) async => MemberAbsence.fromJson(
    await _send(
      'POST',
      '/api/households/$householdId/absences',
      body: {
        'userId': userId,
        'startDate': apiDate(start),
        'endDate': apiDate(end),
        if (reason != null && reason.trim().isNotEmpty) 'reason': reason.trim(),
      },
    ) as Map<String, dynamic>,
  );

  Future<void> deleteAbsence(String householdId, String absenceId) =>
      _send('DELETE', '/api/households/$householdId/absences/$absenceId');

  Future<Member> updateMemberRole(String householdId, String userId, HouseholdRole role) async => Member.fromJson(
    await _send('PUT', '/api/households/$householdId/members/$userId/role', body: {'role': role.apiValue})
        as Map<String, dynamic>,
  );

  /// Tira alguém da casa (só administradores). Um perfil de criança é apagado.
  Future<void> removeMember(String householdId, String userId) =>
      _send('DELETE', '/api/households/$householdId/members/$userId');

  /// Sai da casa. Se for o último membro com conta, a casa é apagada.
  Future<void> leaveHousehold(String householdId) => _send('POST', '/api/households/$householdId/leave');

  /// Passa a administração: a outra pessoa fica administradora e quem passa fica adulto.
  Future<void> transferAdmin(String householdId, String userId) =>
      _send('POST', '/api/households/$householdId/transfer-admin', body: {'userId': userId});

  Future<Member> createChildProfile(String householdId, {required String name, required String pin}) async =>
      Member.fromJson(
        await _send('POST', '/api/households/$householdId/children', body: {'displayName': name, 'pin': pin})
            as Map<String, dynamic>,
      );

  Future<void> setChildPin(String householdId, String userId, String pin) =>
      _send('PUT', '/api/households/$householdId/children/$userId/pin', body: {'pin': pin});

  /// O código da família, com que as crianças entram nos seus perfis (é criado no primeiro pedido).
  Future<String> getFamilyCode(String householdId) async =>
      (await _send('POST', '/api/households/$householdId/family-code') as Map<String, dynamic>)['code'] as String;

  /// O código atual deixa de servir; devolve um novo.
  Future<String> renewFamilyCode(String householdId) async {
    await _send('DELETE', '/api/households/$householdId/family-code');
    return getFamilyCode(householdId);
  }

  Future<List<ChildProfileOption>> childProfiles(String familyCode) async => [
    for (final p in await _send('POST', '/api/auth/child/profiles', body: {'familyCode': familyCode}) as List)
      ChildProfileOption.fromJson(p as Map<String, dynamic>),
  ];

  Future<AuthResult> childLogin(String familyCode, String userId, String pin) async => AuthResult.fromJson(
    await _send('POST', '/api/auth/child/login', body: {'familyCode': familyCode, 'userId': userId, 'pin': pin}),
  );

  // --- Listas de compras ---

  Future<List<ShoppingList>> getLists(String householdId) async =>
      (await _send('GET', '/api/households/$householdId/lists') as List)
          .map((l) => ShoppingList.fromJson(l as Map<String, dynamic>))
          .toList();

  Future<ShoppingList> createList(String householdId, String name) async =>
      ShoppingList.fromJson(await _send('POST', '/api/households/$householdId/lists', body: {'name': name}));

  Future<void> deleteList(String householdId, String listId) =>
      _send('DELETE', '/api/households/$householdId/lists/$listId');

  Future<List<ShoppingItem>> getItems(String householdId, String listId) async =>
      (await _send('GET', _itemsPath(householdId, listId)) as List)
          .map((i) => ShoppingItem.fromJson(i as Map<String, dynamic>))
          .toList();

  /// [id] é gerado na app quando o artigo foi criado sem rede: reenviar não o duplica.
  Future<ShoppingItem> addItem(
    String householdId,
    String listId, {
    String? id,
    required String name,
    String? quantity,
    String? category,
    String? barcode,
  }) async => ShoppingItem.fromJson(
    await _send(
      'POST',
      _itemsPath(householdId, listId),
      body: {
        'id': ?id,
        'name': name,
        if (quantity != null && quantity.isNotEmpty) 'quantity': quantity,
        if (category != null && category.isNotEmpty) 'category': category,
        'barcode': ?barcode,
      },
    ),
  );

  /// O produto deste código de barras, ou null quando não o conhecemos.
  Future<Product?> lookupProduct(String householdId, String barcode) async {
    try {
      return Product.fromJson(await _send('GET', '/api/households/$householdId/products/$barcode'));
    } on ApiException catch (e) {
      if (e.statusCode == 404) return null;
      rethrow;
    }
  }

  Future<ShoppingItem> setItemChecked(String householdId, String listId, String itemId, bool isChecked) async =>
      ShoppingItem.fromJson(
        await _send('PATCH', '${_itemsPath(householdId, listId)}/$itemId', body: {'isChecked': isChecked}),
      );

  Future<void> deleteItem(String householdId, String listId, String itemId) =>
      _send('DELETE', '${_itemsPath(householdId, listId)}/$itemId');

  Future<void> clearChecked(String householdId, String listId) =>
      _send('POST', '${_itemsPath(householdId, listId)}/clear-checked');

  // --- Tarefas ---

  Future<List<Chore>> getChores(String householdId) async =>
      (await _send('GET', '/api/households/$householdId/chores') as List)
          .map((c) => Chore.fromJson(c as Map<String, dynamic>))
          .toList();

  Future<Chore> createChore(String householdId, ChoreDraft draft) async =>
      Chore.fromJson(await _send('POST', '/api/households/$householdId/chores', body: draft.toJson()));

  Future<Chore> updateChore(String householdId, String choreId, ChoreDraft draft) async =>
      Chore.fromJson(await _send('PUT', '/api/households/$householdId/chores/$choreId', body: draft.toJson()));

  Future<void> deleteChore(String householdId, String choreId) =>
      _send('DELETE', '/api/households/$householdId/chores/$choreId');

  /// Conclui a tarefa e devolve a versão atualizada e os pontos ganhos. [photoId]: a foto de prova já enviada.
  Future<(Chore, int)> completeChore(String householdId, String choreId, {String? photoId}) async {
    final json = await _send(
      'POST',
      '/api/households/$householdId/chores/$choreId/complete',
      body: photoId == null ? null : {'photoId': photoId},
    ) as Map<String, dynamic>;
    return (Chore.fromJson(json['chore'] as Map<String, dynamic>), json['pointsEarned'] as int);
  }

  /// Envia a foto de prova de uma tarefa e devolve o id com que se conclui.
  Future<String> uploadChorePhoto(String householdId, Uint8List bytes, String name) async {
    final json = await _send(
      'POST',
      '/api/households/$householdId/chores/photos',
      upload: (field: 'photo', bytes: bytes, filename: name),
    ) as Map<String, dynamic>;
    return json['id'] as String;
  }

  Future<Uint8List> getChorePhoto(String householdId, String photoId) async =>
      (await _execute('GET', '/api/households/$householdId/chores/photos/$photoId')).bodyBytes;

  Future<List<ChoreHistoryItem>> getChoreHistory(String householdId, {int days = 30}) async => [
    for (final i in await _send('GET', '/api/households/$householdId/chores/history?days=$days') as List)
      ChoreHistoryItem.fromJson(i as Map<String, dynamic>),
  ];

  /// Não aceita uma tarefa feita: os pontos deixam de contar e a tarefa volta para quem a fez.
  Future<ChoreHistoryItem> rejectChoreCompletion(String householdId, String completionId) async =>
      ChoreHistoryItem.fromJson(
        await _send('POST', '/api/households/$householdId/chores/completions/$completionId/reject'),
      );

  /// Marca ou desmarca um passo de uma rotina. Se era o último, a rotina fica concluída ([completed]) e dá pontos.
  Future<({Chore chore, bool completed, int points})> setChoreStep(
    String householdId,
    String choreId,
    int index, {
    required bool done,
  }) async {
    final json = await _send(
      'POST',
      '/api/households/$householdId/chores/$choreId/steps/$index',
      body: {'done': done},
    ) as Map<String, dynamic>;
    return (
      chore: Chore.fromJson(json['chore'] as Map<String, dynamic>),
      completed: json['completed'] as bool,
      points: json['pointsEarned'] as int,
    );
  }

  Future<List<MemberChoreStats>> getChoreStats(String householdId, {int days = 30}) async {
    final json = await _send('GET', '/api/households/$householdId/chores/stats?days=$days') as Map<String, dynamic>;
    return (json['members'] as List).map((m) => MemberChoreStats.fromJson(m as Map<String, dynamic>)).toList();
  }

  // --- Histórico ---

  Future<List<ActivityItem>> getActivity(String householdId) async => [
    for (final a in await _send('GET', '/api/households/$householdId/activity') as List)
      ActivityItem.fromJson(a as Map<String, dynamic>),
  ];

  /// Desfaz uma alteração (o que foi criado sai, o que mudou ou foi apagado volta ao que era).
  Future<void> undoActivity(String householdId, int entryId) =>
      _send('POST', '/api/households/$householdId/activity/$entryId/undo');

  // --- Mesada ---

  Future<List<Allowance>> getAllowances(String householdId) async => [
    for (final a in await _send('GET', '/api/households/$householdId/allowances') as List)
      Allowance.fromJson(a as Map<String, dynamic>),
  ];

  Future<Allowance> saveAllowance(
    String householdId,
    String userId, {
    required int amountCents,
    int bonusPerChoreCents = 0,
    AllowancePeriod period = AllowancePeriod.weekly,
  }) async => Allowance.fromJson(
    await _send(
      'PUT',
      '/api/households/$householdId/allowances/$userId',
      body: {'amountCents': amountCents, 'bonusPerChoreCents': bonusPerChoreCents, 'period': period.apiValue},
    ),
  );

  Future<void> stopAllowance(String householdId, String userId) =>
      _send('DELETE', '/api/households/$householdId/allowances/$userId');

  /// Marca o período que começa em [periodStart] como pago (com o valor que tem agora).
  Future<Allowance> payAllowance(String householdId, String userId, DateTime periodStart) async => Allowance.fromJson(
    await _send(
      'POST',
      '/api/households/$householdId/allowances/$userId/payments',
      body: {'periodStart': apiDate(periodStart)},
    ),
  );

  Future<void> undoAllowancePayment(String householdId, String userId, String paymentId) =>
      _send('DELETE', '/api/households/$householdId/allowances/$userId/payments/$paymentId');

  // --- Calendário ---

  /// Eventos, ocorrências e feriados entre dois dias (inclusive; no máximo 92 dias).
  Future<CalendarRange> getCalendar(String householdId, DateTime from, DateTime to) async {
    final query = 'from=${apiDate(from)}&to=${apiDate(to)}';
    final json = await _send('GET', '${_eventsPath(householdId)}?$query') as Map<String, dynamic>;
    return CalendarRange.fromJson(json);
  }

  Future<CalendarEvent> createEvent(String householdId, CalendarEventDraft draft) async =>
      CalendarEvent.fromJson(await _send('POST', _eventsPath(householdId), body: draft.toJson()));

  Future<CalendarEvent> updateEvent(String householdId, String eventId, CalendarEventDraft draft) async =>
      CalendarEvent.fromJson(await _send('PUT', '${_eventsPath(householdId)}/$eventId', body: draft.toJson()));

  Future<void> deleteEvent(String householdId, String eventId) =>
      _send('DELETE', '/api/households/$householdId/events/$eventId');

  /// Altera só a ocorrência de [date] de uma série ou, com [following], essa e as seguintes.
  Future<CalendarEvent> updateOccurrence(
    String householdId,
    String eventId,
    DateTime date,
    CalendarEventDraft draft, {
    bool following = false,
  }) async => CalendarEvent.fromJson(
    await _send('PUT', _occurrencePath(householdId, eventId, date, following), body: draft.toJson()),
  );

  /// Apaga só a ocorrência de [date] de uma série ou, com [following], essa e as seguintes.
  Future<void> deleteOccurrence(String householdId, String eventId, DateTime date, {bool following = false}) =>
      _send('DELETE', _occurrencePath(householdId, eventId, date, following));

  /// O endereço pessoal para subscrever o calendário da casa (é criado no primeiro pedido).
  Future<CalendarFeed> getCalendarFeed(String householdId) async =>
      CalendarFeed.fromJson(await _send('POST', '/api/households/$householdId/calendar-feed'));

  /// Desativa o endereço atual e devolve um novo.
  Future<CalendarFeed> renewCalendarFeed(String householdId) async {
    await _send('DELETE', '/api/households/$householdId/calendar-feed');
    return getCalendarFeed(householdId);
  }

  // --- Google Calendar e Outlook ---

  Future<CalendarSyncOverview> getCalendarSync(String householdId) async => CalendarSyncOverview.fromJson(
    await _send('GET', '/api/households/$householdId/calendar-sync') as Map<String, dynamic>,
  );

  /// A página onde a pessoa autoriza o Kubico na sua conta (abre-se no navegador).
  Future<Uri> authorizeCalendarSync(String householdId, CalendarProvider provider) async {
    final json = await _send('POST', '/api/households/$householdId/calendar-sync/${provider.name}/authorize');
    return Uri.parse((json as Map<String, dynamic>)['url'] as String);
  }

  Future<CalendarConnection> syncCalendarConnection(String householdId, String connectionId) async =>
      CalendarConnection.fromJson(
        await _send('POST', '/api/households/$householdId/calendar-sync/$connectionId/sync') as Map<String, dynamic>,
      );

  /// Desliga a conta: o calendário do Kubico é apagado lá; os eventos ficam no Kubico.
  Future<void> disconnectCalendar(String householdId, String connectionId) =>
      _send('DELETE', '/api/households/$householdId/calendar-sync/$connectionId');

  // --- Recompensas ---

  Future<RewardsOverview> getRewards(String householdId) async =>
      RewardsOverview.fromJson(await _send('GET', '/api/households/$householdId/rewards') as Map<String, dynamic>);

  Future<Reward> createReward(String householdId, RewardDraft draft) async =>
      Reward.fromJson(await _send('POST', '/api/households/$householdId/rewards', body: draft.toJson()));

  Future<Reward> updateReward(String householdId, String rewardId, RewardDraft draft) async =>
      Reward.fromJson(await _send('PUT', '/api/households/$householdId/rewards/$rewardId', body: draft.toJson()));

  Future<void> deleteReward(String householdId, String rewardId) =>
      _send('DELETE', '/api/households/$householdId/rewards/$rewardId');

  /// Pede a recompensa: fica à espera de um adulto, ou aprovada logo se quem pede for adulto.
  Future<RewardRedemption> redeemReward(String householdId, String rewardId) async => RewardRedemption.fromJson(
    await _send('POST', '/api/households/$householdId/rewards/$rewardId/redeem') as Map<String, dynamic>,
  );

  Future<RewardRedemption> decideRedemption(String householdId, String redemptionId, {required bool approve}) async {
    final action = approve ? 'approve' : 'reject';
    final json = await _send('POST', '/api/households/$householdId/redemptions/$redemptionId/$action');
    return RewardRedemption.fromJson(json as Map<String, dynamic>);
  }

  /// Cancela um pedido que ainda está à espera; os pontos voltam.
  Future<void> cancelRedemption(String householdId, String redemptionId) =>
      _send('DELETE', '/api/households/$householdId/redemptions/$redemptionId');

  // --- Avisos ---

  /// Os avisos mais recentes (de todas as casas) e quantos estão por ler.
  Future<(List<AppNotification>, int)> getNotifications() async {
    final json = await _send('GET', '/api/notifications') as Map<String, dynamic>;
    final items = json['items'] as List;
    return ([for (final n in items) AppNotification.fromJson(n as Map<String, dynamic>)], json['unreadCount'] as int);
  }

  /// Marca como lidos os avisos indicados ou, sem [ids], todos.
  Future<void> markNotificationsRead([List<String>? ids]) =>
      _send('POST', '/api/notifications/read', body: {'ids': ?ids});

  Future<NotificationPreferences> getNotificationPreferences() async =>
      NotificationPreferences.fromJson(await _send('GET', '/api/notifications/preferences') as Map<String, dynamic>);

  Future<NotificationPreferences> saveNotificationPreferences(NotificationPreferences preferences) async =>
      NotificationPreferences.fromJson(
        await _send('PUT', '/api/notifications/preferences', body: preferences.toJson()) as Map<String, dynamic>,
      );

  /// O token push deste telemóvel (do Firebase), para receber os avisos fora da app.
  Future<void> registerPushDevice(String token, String platform) =>
      _send('POST', '/api/notifications/devices', body: {'token': token, 'platform': platform});

  Future<void> unregisterPushDevice(String token) =>
      _send('POST', '/api/notifications/devices/unregister', body: {'token': token});

  String _occurrencePath(String householdId, String eventId, DateTime date, bool following) =>
      '${_eventsPath(householdId)}/$eventId/occurrences/${apiDate(date)}?scope=${following ? 'following' : 'this'}';

  String _eventsPath(String householdId) => '/api/households/$householdId/events';

  String _itemsPath(String householdId, String listId) => '/api/households/$householdId/lists/$listId/items';

  // --- HTTP ---

  /// Pedido com resposta em JSON (ou vazia). [upload] envia um ficheiro (multipart) em vez de [body].
  Future<dynamic> _send(String method, String path, {Map<String, dynamic>? body, _Upload? upload}) async =>
      _decode((await _execute(method, path, body: body, upload: upload)).bodyBytes);

  /// Faz o pedido com a sessão; num 401 renova a sessão e repete uma vez. Erros viram [ApiException].
  Future<http.Response> _execute(
    String method,
    String path, {
    Map<String, dynamic>? body,
    _Upload? upload,
    bool retry = true,
  }) async {
    final uri = Uri.parse('$baseUrl$path');
    final http.BaseRequest request;
    if (upload != null) {
      request = http.MultipartRequest(method, uri)
        ..files.add(http.MultipartFile.fromBytes(upload.field, upload.bytes, filename: upload.filename));
    } else {
      final plain = http.Request(method, uri);
      if (body != null) {
        plain.headers['Content-Type'] = 'application/json';
        plain.body = jsonEncode(body);
      }
      request = plain;
    }
    request.headers['Accept'] = 'application/json';
    if (accessToken != null) request.headers['Authorization'] = 'Bearer $accessToken';
    if (deviceKind != null) request.headers['X-Kubico-Device'] = deviceKind!;

    final http.Response response;
    try {
      response = await http.Response.fromStream(await _http.send(request));
    } on Exception {
      throw const ApiException(0, 'Sem ligação ao servidor. Verifica a tua internet.');
    }
    if (response.statusCode >= 200 && response.statusCode < 300) return response;

    final renew = onUnauthorized;
    if (response.statusCode == 401 && retry && accessToken != null && renew != null && !_authPaths.contains(path)) {
      if (await renew()) return _execute(method, path, body: body, upload: upload, retry: false);
    }

    throw ApiException(response.statusCode, _errorMessage(response.statusCode, _decode(response.bodyBytes)));
  }

  static dynamic _decode(List<int> bytes) {
    if (bytes.isEmpty) return null;
    try {
      return jsonDecode(utf8.decode(bytes));
    } on FormatException {
      return null; // Corpo que não é JSON (ex.: página de erro de um proxy).
    }
  }

  static const _genericTitles = {
    'One or more validation errors occurred.',
    'Bad Request',
    'Unauthorized',
    'Forbidden',
    'Not Found',
    'Conflict',
    'An error occurred while processing your request.',
  };

  static String _errorMessage(int status, dynamic body) {
    if (body is Map<String, dynamic>) {
      // Erros de validação: mostra a primeira mensagem.
      final errors = body['errors'];
      if (errors is Map && errors.isNotEmpty) {
        final first = errors.values.first;
        if (first is List && first.isNotEmpty) return first.first.toString();
      }
      // O título é a mensagem do servidor, em português; os títulos genéricos do ASP.NET (em inglês) não servem.
      final title = body['title'];
      if (title is String && title.isNotEmpty && !_genericTitles.contains(title)) return title;
    }
    return switch (status) {
      400 => 'Verifica os dados introduzidos.',
      401 => 'A tua sessão expirou. Entra novamente.',
      403 => 'Não tens permissão para fazer isto.',
      404 => 'Não encontrado.',
      _ => 'Ocorreu um erro inesperado ($status).',
    };
  }
}
