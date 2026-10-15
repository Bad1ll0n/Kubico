import 'dart:convert';

import 'package:http/http.dart' as http;

import 'models.dart';

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
  ApiClient({required this.baseUrl, http.Client? httpClient}) : _http = httpClient ?? http.Client();

  final String baseUrl;
  final http.Client _http;

  /// Token JWT da sessão atual; null quando ninguém tem sessão iniciada.
  String? accessToken;

  // --- Autenticação ---

  Future<AuthResult> register(String email, String password, String displayName) async => AuthResult.fromJson(
    await _send('POST', '/api/auth/register', body: {'email': email, 'password': password, 'displayName': displayName}),
  );

  Future<AuthResult> login(String email, String password) async =>
      AuthResult.fromJson(await _send('POST', '/api/auth/login', body: {'email': email, 'password': password}));

  Future<Me> getMe() async => Me.fromJson(await _send('GET', '/api/me'));

  // --- Agregados ---

  Future<HouseholdSummary> createHousehold(String name) async =>
      HouseholdSummary.fromJson(await _send('POST', '/api/households', body: {'name': name}));

  Future<HouseholdDetails> getHousehold(String householdId) async =>
      HouseholdDetails.fromJson(await _send('GET', '/api/households/$householdId'));

  Future<Invite> createInvite(String householdId, HouseholdRole role) async =>
      Invite.fromJson(await _send('POST', '/api/households/$householdId/invites', body: {'role': role.apiValue}));

  Future<HouseholdSummary> acceptInvite(String code) async =>
      HouseholdSummary.fromJson(await _send('POST', '/api/invites/${Uri.encodeComponent(code.trim())}/accept'));

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

  Future<ShoppingItem> addItem(
    String householdId,
    String listId, {
    required String name,
    String? quantity,
    String? category,
  }) async => ShoppingItem.fromJson(
    await _send(
      'POST',
      _itemsPath(householdId, listId),
      body: {
        'name': name,
        if (quantity != null && quantity.isNotEmpty) 'quantity': quantity,
        if (category != null && category.isNotEmpty) 'category': category,
      },
    ),
  );

  Future<ShoppingItem> setItemChecked(String householdId, String listId, String itemId, bool isChecked) async =>
      ShoppingItem.fromJson(
        await _send('PATCH', '${_itemsPath(householdId, listId)}/$itemId', body: {'isChecked': isChecked}),
      );

  Future<void> deleteItem(String householdId, String listId, String itemId) =>
      _send('DELETE', '${_itemsPath(householdId, listId)}/$itemId');

  Future<void> clearChecked(String householdId, String listId) =>
      _send('POST', '${_itemsPath(householdId, listId)}/clear-checked');

  String _itemsPath(String householdId, String listId) => '/api/households/$householdId/lists/$listId/items';

  // --- HTTP ---

  Future<dynamic> _send(String method, String path, {Map<String, dynamic>? body}) async {
    final request = http.Request(method, Uri.parse('$baseUrl$path'))..headers['Accept'] = 'application/json';
    if (accessToken != null) request.headers['Authorization'] = 'Bearer $accessToken';
    if (body != null) {
      request.headers['Content-Type'] = 'application/json';
      request.body = jsonEncode(body);
    }

    final http.Response response;
    try {
      response = await http.Response.fromStream(await _http.send(request));
    } on Exception {
      throw const ApiException(0, 'Sem ligação ao servidor. Verifica a tua internet.');
    }

    dynamic decoded;
    if (response.bodyBytes.isNotEmpty) {
      try {
        decoded = jsonDecode(utf8.decode(response.bodyBytes));
      } on FormatException {
        decoded = null; // Corpo que não é JSON (ex.: página de erro de um proxy).
      }
    }
    if (response.statusCode >= 200 && response.statusCode < 300) return decoded;

    throw ApiException(response.statusCode, _errorMessage(response.statusCode, decoded));
  }

  static String _errorMessage(int status, dynamic body) {
    if (body is Map<String, dynamic>) {
      // Erros de validação: mostra a primeira mensagem.
      final errors = body['errors'];
      if (errors is Map && errors.isNotEmpty) {
        final first = errors.values.first;
        if (first is List && first.isNotEmpty) return first.first.toString();
      }
      final title = body['title'];
      if (title is String && title.isNotEmpty && status != 400) return title;
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
