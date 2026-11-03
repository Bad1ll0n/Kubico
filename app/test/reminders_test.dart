import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/api/models.dart';
import 'package:kubico/src/notifications/open_notification.dart';

void main() {
  test('o lembrete de um evento e a hora de lembrar uma tarefa vão no pedido e voltam na resposta (F4-11)', () {
    final draft = CalendarEventDraft(
      title: 'Natação',
      start: DateTime(2026, 11, 10, 17, 30),
      end: DateTime(2026, 11, 10, 18, 30),
      reminderMinutes: const [30],
    );
    final chore = const ChoreDraft(title: 'Lixo', remindAt: (hour: 20, minute: 5));
    final noReminder = CalendarEventDraft(title: 'X', start: DateTime(2026), end: DateTime(2026));

    expect(draft.toJson()['reminderMinutes'], [30]);
    expect(noReminder.toJson().containsKey('reminderMinutes'), isFalse);
    expect(chore.toJson()['remindAt'], '20:05:00');
    expect(
      Chore.fromJson({
        'id': 'c1',
        'householdId': 'h1',
        'title': 'Lixo',
        'notes': null,
        'points': 0,
        'dueDate': null,
        'recurrence': 'None',
        'assigneeUserId': null,
        'rotationUserIds': <String>[],
        'isDone': false,
        'updatedAt': '2026-11-10T10:00:00Z',
        'remindAt': '20:05:00',
      }).remindAt,
      (hour: 20, minute: 5),
    );
  });

  test('os lembretes e os resumos abrem a área certa', () {
    expect(NotificationTarget.of('EventReminder', 'h1')?.module, HouseholdModule.calendar);
    expect(NotificationTarget.of('ChoreReminder', 'h1')?.module, HouseholdModule.chores);
    expect(NotificationTarget.of('WeeklySummary', 'h1')?.module, HouseholdModule.chores);
    expect(NotificationTarget.of('AllowanceDue', 'h1')?.module, HouseholdModule.rewards);
    expect(NotificationTarget.of('MorningSummary', 'h1')?.module, isNull, reason: 'o resumo da manhã abre o Hoje');
  });

  test('a rotação justa vai no pedido só quando está ligada (F4-23)', () {
    expect(const ChoreDraft(title: 'Lixo', fairRotation: true).toJson()['fairRotation'], isTrue);
    expect(const ChoreDraft(title: 'Lixo').toJson().containsKey('fairRotation'), isFalse);
  });
}
