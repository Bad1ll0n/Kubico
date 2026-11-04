import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';

void main() {
  Future<String> messageFor(int status, Map<String, dynamic> body) async {
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((_) async => http.Response.bytes(utf8.encode(jsonEncode(body)), status)),
    );
    try {
      await api.getMe();
    } on ApiException catch (e) {
      return e.message;
    }
    fail('Devia ter dado erro.');
  }

  test('os erros mostram a mensagem do servidor, ou uma em português quando ela é genérica', () async {
    expect(await messageFor(400, {'title': 'A palavra-passe não está certa.'}), 'A palavra-passe não está certa.');
    expect(
      await messageFor(400, {'title': 'One or more validation errors occurred.'}),
      'Verifica os dados introduzidos.',
    );
    expect(
      await messageFor(400, {
        'title': 'One or more validation errors occurred.',
        'errors': {
          'Name': ['O nome é obrigatório.'],
        },
      }),
      'O nome é obrigatório.',
    );
    expect(await messageFor(404, {'title': 'Not Found'}), 'Não encontrado.');
    expect(await messageFor(409, {'title': 'Esse período já está pago.'}), 'Esse período já está pago.');
  });
}
