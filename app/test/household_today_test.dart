import 'package:flutter_test/flutter_test.dart';
import 'package:kubico/src/api/models.dart';

HouseholdDetails _household(int? offset) => HouseholdDetails(
  id: 'h1',
  name: 'Casa',
  timeZoneId: 'Europe/Lisbon',
  myRole: HouseholdRole.adult,
  members: const [],
  utcOffsetMinutes: offset,
);

void main() {
  test('o dia é o da casa, não o do telemóvel (QA-11)', () {
    // 22:00 no Brasil (UTC−3) a 10/11 é já 01:00 de 11/11 em Lisboa (UTC+0 no inverno).
    final now = DateTime.utc(2026, 11, 11, 1);

    expect(_household(0).todayAt(now), DateTime(2026, 11, 11));
    expect(_household(-180).todayAt(now), DateTime(2026, 11, 10));
    expect(_household(60).todayAt(DateTime.utc(2026, 7, 1, 23, 30)), DateTime(2026, 7, 2), reason: 'Lisboa no verão');
  });
}
