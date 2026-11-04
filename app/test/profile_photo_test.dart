import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/ui/design/components.dart';
import 'package:provider/provider.dart';

/// Um PNG de 1 × 1 píxel.
final _png = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
);

void main() {
  tearDown(AvatarPhotos.clear);

  test('a fotografia vem com o membro e com a conta, e sem ela fica o emoji', () {
    final member = Member.fromJson({
      'userId': 'mae',
      'displayName': 'Mãe',
      'role': 'Admin',
      'color': '#2F64A8',
      'avatar': '🦊',
      'photo': 'v1',
    });
    final child = Member.fromJson({'userId': 'rita', 'displayName': 'Rita', 'role': 'Child', 'color': '#A9500B'});
    final user = User.fromJson({'id': 'mae', 'email': 'm@k.test', 'displayName': 'Mãe', 'photo': 'v2'});

    expect(member.photo, (userId: 'mae', version: 'v1'));
    expect(child.photo, isNull);
    expect(user.photo, (userId: 'mae', version: 'v2'));
    expect(User.fromJson(user.toJson()).photoVersion, 'v2');
  });

  testWidgets('o avatar mostra a fotografia (pedida uma vez) e a inicial enquanto não chega', (tester) async {
    var requests = 0;
    final api = ApiClient(
      baseUrl: 'http://api',
      httpClient: MockClient((request) async {
        requests++;
        return request.url.path == '/api/users/mae/photo'
            ? http.Response.bytes(_png, 200, headers: {'content-type': 'image/png'})
            : http.Response('', 404);
      }),
    )..accessToken = 't';
    const photo = (userId: 'mae', version: 'v1');

    await tester.pumpWidget(
      Provider.value(
        value: api,
        child: const MaterialApp(
          home: Scaffold(
            body: Row(
              children: [
                MemberAvatar(name: 'Mãe', color: '#2F64A8', photo: photo, size: 40),
                MemberAvatar(name: 'Mãe', color: '#2F64A8', photo: photo, size: 24),
              ],
            ),
          ),
        ),
      ),
    );
    expect(find.text('M'), findsNWidgets(2));
    await tester.pumpAndSettle();

    expect(find.byType(Image), findsNWidgets(2));
    expect(requests, 1);
    expect(find.bySemanticsLabel('Mãe'), findsNWidgets(2));
  });
}
