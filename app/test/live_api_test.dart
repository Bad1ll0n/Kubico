// Testes contra uma API real a correr (não correm por omissão).
//
//   KUBICO_API_URL=http://localhost:5281 flutter test test/live_api_test.dart
@Tags(['live'])
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/api/api_client.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/realtime/household_realtime.dart';

void main() {
  final baseUrl = Platform.environment['KUBICO_API_URL'];

  test('um membro recebe em tempo real o artigo que outro adiciona', () async {
    final suffix = DateTime.now().microsecondsSinceEpoch;
    final admin = ApiClient(baseUrl: baseUrl!);
    final partner = ApiClient(baseUrl: baseUrl);

    admin.accessToken = (await admin.register('admin$suffix@kubico.test', 'palavra-passe', 'Admin')).accessToken;
    partner.accessToken = (await partner.register(
      'partner$suffix@kubico.test',
      'palavra-passe',
      'Parceiro',
    )).accessToken;

    final household = await admin.createHousehold('Casa $suffix');
    final invite = await admin.createInvite(household.id, HouseholdRole.adult);
    await partner.acceptInvite(invite.code);
    final list = await admin.createList(household.id, 'Supermercado');

    final realtime = HouseholdRealtime(baseUrl: baseUrl, accessToken: () => partner.accessToken!);
    final received = realtime.events
        .where((e) => e is ShoppingItemUpserted)
        .cast<ShoppingItemUpserted>()
        .first
        .timeout(const Duration(seconds: 10));
    await realtime.connect(household.id);

    await admin.addItem(household.id, list.id, name: 'Café', quantity: '1');

    final event = await received;
    expect(event.item.name, 'Café');
    expect(event.item.listId, list.id);
    await realtime.dispose();
  }, skip: baseUrl == null ? 'Define KUBICO_API_URL para correr contra uma API real.' : false);

  test('tarefa em rotação passa ao membro seguinte e os outros veem em tempo real', () async {
    final suffix = DateTime.now().microsecondsSinceEpoch;
    final admin = ApiClient(baseUrl: baseUrl!);
    final partner = ApiClient(baseUrl: baseUrl);

    final adminAuth = await admin.register('admin-c$suffix@kubico.test', 'palavra-passe', 'Admin');
    admin.accessToken = adminAuth.accessToken;
    final partnerAuth = await partner.register('partner-c$suffix@kubico.test', 'palavra-passe', 'Parceiro');
    partner.accessToken = partnerAuth.accessToken;

    final household = await admin.createHousehold('Casa $suffix');
    final invite = await admin.createInvite(household.id, HouseholdRole.adult);
    await partner.acceptInvite(invite.code);

    final chore = await admin.createChore(
      household.id,
      ChoreDraft(
        title: 'Lixo',
        points: 3,
        dueDate: DateTime.now(),
        recurrence: ChoreRecurrence.weekly,
        rotationUserIds: [adminAuth.user.id, partnerAuth.user.id],
      ),
    );

    final realtime = HouseholdRealtime(baseUrl: baseUrl, accessToken: () => partner.accessToken!);
    final received = realtime.events
        .where((e) => e is ChoreUpserted)
        .cast<ChoreUpserted>()
        .first
        .timeout(const Duration(seconds: 10));
    await realtime.connect(household.id);

    final (updated, points) = await admin.completeChore(household.id, chore.id);

    expect(points, 3);
    expect(updated.assigneeUserId, partnerAuth.user.id);
    expect((await received).chore.assigneeUserId, partnerAuth.user.id);
    final stats = await partner.getChoreStats(household.id);
    expect(stats.firstWhere((s) => s.userId == adminAuth.user.id).points, 3);
    await realtime.dispose();
  }, skip: baseUrl == null ? 'Define KUBICO_API_URL para correr contra uma API real.' : false);
}
