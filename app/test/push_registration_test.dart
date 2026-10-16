import 'dart:async';
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/notifications/push_registration.dart';

/// Um Firebase de mentira: dá o token que se quiser e deixa simular renovações.
class _FakeSource implements PushTokenSource {
  _FakeSource({this.token = 'token-1'});

  @override
  String? get platform => 'android';
  String? token;
  final refreshes = StreamController<String>.broadcast();
  var deleted = false;

  @override
  Future<String?> getToken() async => token;

  @override
  Stream<String> get onTokenRefresh => refreshes.stream;

  @override
  Future<void> deleteToken() async => deleted = true;
}

void main() {
  late List<String> requests;
  late bool online;
  late ApiClient api;

  setUp(() {
    requests = [];
    online = true;
    api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((request) async {
        if (!online) throw http.ClientException('Sem rede');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        requests.add('${request.url.path} ${body['token']} ${body['platform'] ?? ''}'.trim());
        return http.Response('', 204);
      }),
    )..accessToken = 't';
  });

  test('regista o telemóvel ao entrar, outra vez quando o token muda, e apaga-o ao sair', () async {
    final source = _FakeSource();
    final push = PushRegistration(api: api, source: source);

    await push.register();
    source.refreshes.add('token-2');
    await Future<void>.delayed(Duration.zero);
    await push.unregister();

    expect(requests, [
      '/api/notifications/devices token-1 android',
      '/api/notifications/devices token-2 android',
      '/api/notifications/devices/unregister token-2',
    ]);
    expect(source.deleted, isTrue);
  });

  test('sem push neste dispositivo, ou com a autorização recusada, não fala com o servidor', () async {
    await PushRegistration(api: api, source: const NoPushTokenSource()).register();
    await PushRegistration(api: api, source: _FakeSource(token: null)).register();

    expect(requests, isEmpty);
  });

  test('sem rede, entrar e sair continuam a funcionar', () async {
    online = false;
    final source = _FakeSource();
    final push = PushRegistration(api: api, source: source);

    await push.register();
    await push.unregister();

    expect(source.deleted, isTrue);
  });
}
