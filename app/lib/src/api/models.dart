/// Modelos que espelham os DTOs da API (backend/src/Kubico.Api).
library;

enum HouseholdRole {
  admin('Admin', 'Administrador'),
  adult('Adult', 'Adulto'),
  child('Child', 'Criança'),
  guest('Guest', 'Convidado');

  const HouseholdRole(this.apiValue, this.label);

  final String apiValue;
  final String label;

  bool get canManageContent => this == admin || this == adult;
  bool get canManageMembers => this == admin;

  static HouseholdRole fromApi(String value) => values.firstWhere((r) => r.apiValue == value, orElse: () => guest);
}

class User {
  const User({required this.id, required this.email, required this.displayName});

  final String id;
  final String email;
  final String displayName;

  factory User.fromJson(Map<String, dynamic> json) =>
      User(id: json['id'] as String, email: json['email'] as String, displayName: json['displayName'] as String);
}

class AuthResult {
  const AuthResult({required this.accessToken, required this.user});

  final String accessToken;
  final User user;

  factory AuthResult.fromJson(Map<String, dynamic> json) =>
      AuthResult(accessToken: json['accessToken'] as String, user: User.fromJson(json['user'] as Map<String, dynamic>));
}

class HouseholdSummary {
  const HouseholdSummary({required this.id, required this.name, required this.role});

  final String id;
  final String name;
  final HouseholdRole role;

  factory HouseholdSummary.fromJson(Map<String, dynamic> json) => HouseholdSummary(
    id: json['id'] as String,
    name: json['name'] as String,
    role: HouseholdRole.fromApi(json['role'] as String),
  );
}

class Me {
  const Me({required this.user, required this.households});

  final User user;
  final List<HouseholdSummary> households;

  factory Me.fromJson(Map<String, dynamic> json) => Me(
    user: User.fromJson(json['user'] as Map<String, dynamic>),
    households: (json['households'] as List).map((h) => HouseholdSummary.fromJson(h as Map<String, dynamic>)).toList(),
  );
}

class Member {
  const Member({required this.userId, required this.displayName, required this.role, required this.color});

  final String userId;
  final String displayName;
  final HouseholdRole role;
  final String color;

  factory Member.fromJson(Map<String, dynamic> json) => Member(
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
    role: HouseholdRole.fromApi(json['role'] as String),
    color: json['color'] as String,
  );
}

class HouseholdDetails {
  const HouseholdDetails({
    required this.id,
    required this.name,
    required this.timeZoneId,
    required this.myRole,
    required this.members,
  });

  final String id;
  final String name;
  final String timeZoneId;
  final HouseholdRole myRole;
  final List<Member> members;

  Member? member(String? userId) {
    for (final m in members) {
      if (m.userId == userId) return m;
    }
    return null;
  }

  factory HouseholdDetails.fromJson(Map<String, dynamic> json) => HouseholdDetails(
    id: json['id'] as String,
    name: json['name'] as String,
    timeZoneId: json['timeZoneId'] as String? ?? 'Europe/Lisbon',
    myRole: HouseholdRole.fromApi(json['myRole'] as String),
    members: (json['members'] as List).map((m) => Member.fromJson(m as Map<String, dynamic>)).toList(),
  );
}

class Invite {
  const Invite({required this.code, required this.role, required this.expiresAt});

  final String code;
  final HouseholdRole role;
  final DateTime expiresAt;

  factory Invite.fromJson(Map<String, dynamic> json) => Invite(
    code: json['code'] as String,
    role: HouseholdRole.fromApi(json['role'] as String),
    expiresAt: DateTime.parse(json['expiresAt'] as String).toLocal(),
  );
}

class ShoppingList {
  const ShoppingList({
    required this.id,
    required this.householdId,
    required this.name,
    required this.itemCount,
    required this.checkedCount,
  });

  final String id;
  final String householdId;
  final String name;
  final int itemCount;
  final int checkedCount;

  int get pendingCount => itemCount - checkedCount;

  Map<String, dynamic> toJson() => {
    'id': id,
    'householdId': householdId,
    'name': name,
    'itemCount': itemCount,
    'checkedCount': checkedCount,
  };

  factory ShoppingList.fromJson(Map<String, dynamic> json) => ShoppingList(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    name: json['name'] as String,
    itemCount: json['itemCount'] as int,
    checkedCount: json['checkedCount'] as int,
  );
}

class ShoppingItem {
  const ShoppingItem({
    required this.id,
    required this.listId,
    required this.name,
    required this.quantity,
    required this.category,
    required this.isChecked,
    required this.addedByUserId,
    required this.createdAt,
    required this.updatedAt,
  });

  final String id;
  final String listId;
  final String name;
  final String? quantity;
  final String? category;
  final bool isChecked;
  final String addedByUserId;
  final DateTime createdAt;
  final DateTime updatedAt;

  ShoppingItem copyWith({bool? isChecked}) => ShoppingItem(
    id: id,
    listId: listId,
    name: name,
    quantity: quantity,
    category: category,
    isChecked: isChecked ?? this.isChecked,
    addedByUserId: addedByUserId,
    createdAt: createdAt,
    updatedAt: updatedAt,
  );

  Map<String, dynamic> toJson() => {
    'id': id,
    'listId': listId,
    'name': name,
    'quantity': quantity,
    'category': category,
    'isChecked': isChecked,
    'addedByUserId': addedByUserId,
    'createdAt': createdAt.toIso8601String(),
    'updatedAt': updatedAt.toIso8601String(),
  };

  factory ShoppingItem.fromJson(Map<String, dynamic> json) => ShoppingItem(
    id: json['id'] as String,
    listId: json['listId'] as String,
    name: json['name'] as String,
    quantity: json['quantity'] as String?,
    category: json['category'] as String?,
    isChecked: json['isChecked'] as bool,
    addedByUserId: json['addedByUserId'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String),
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );
}

enum ChoreRecurrence {
  none('None', 'Não se repete'),
  daily('Daily', 'Todos os dias'),
  weekly('Weekly', 'Todas as semanas'),
  monthly('Monthly', 'Todos os meses');

  const ChoreRecurrence(this.apiValue, this.label);

  final String apiValue;
  final String label;

  static ChoreRecurrence fromApi(String value) => values.firstWhere((r) => r.apiValue == value, orElse: () => none);
}

/// Data sem hora, no formato da API (yyyy-MM-dd).
String apiDate(DateTime d) =>
    '${d.year.toString().padLeft(4, '0')}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

/// Uma tarefa da casa. As datas de prazo são dias (sem hora).
class Chore {
  const Chore({
    required this.id,
    required this.householdId,
    required this.title,
    required this.notes,
    required this.points,
    required this.dueDate,
    required this.recurrence,
    required this.assigneeUserId,
    required this.rotationUserIds,
    required this.isDone,
    required this.updatedAt,
  });

  final String id;
  final String householdId;
  final String title;
  final String? notes;
  final int points;
  final DateTime? dueDate;
  final ChoreRecurrence recurrence;
  final String? assigneeUserId;
  final List<String> rotationUserIds;
  final bool isDone;
  final DateTime updatedAt;

  bool get rotates => rotationUserIds.isNotEmpty;

  factory Chore.fromJson(Map<String, dynamic> json) => Chore(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    title: json['title'] as String,
    notes: json['notes'] as String?,
    points: json['points'] as int,
    dueDate: json['dueDate'] == null ? null : DateTime.parse(json['dueDate'] as String),
    recurrence: ChoreRecurrence.fromApi(json['recurrence'] as String),
    assigneeUserId: json['assigneeUserId'] as String?,
    rotationUserIds: (json['rotationUserIds'] as List).cast<String>(),
    isDone: json['isDone'] as bool,
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );
}

/// Dados para criar ou editar uma tarefa.
class ChoreDraft {
  const ChoreDraft({
    required this.title,
    this.notes,
    this.points = 0,
    this.dueDate,
    this.recurrence = ChoreRecurrence.none,
    this.assigneeUserId,
    this.rotationUserIds = const [],
  });

  final String title;
  final String? notes;
  final int points;
  final DateTime? dueDate;
  final ChoreRecurrence recurrence;
  final String? assigneeUserId;
  final List<String> rotationUserIds;

  factory ChoreDraft.from(Chore chore) => ChoreDraft(
    title: chore.title,
    notes: chore.notes,
    points: chore.points,
    dueDate: chore.dueDate,
    recurrence: chore.recurrence,
    assigneeUserId: chore.assigneeUserId,
    rotationUserIds: chore.rotationUserIds,
  );

  Map<String, dynamic> toJson() => {
    'title': title,
    if (notes != null && notes!.isNotEmpty) 'notes': notes,
    'points': points,
    if (dueDate != null) 'dueDate': apiDate(dueDate!),
    'recurrence': recurrence.apiValue,
    if (assigneeUserId != null) 'assigneeUserId': assigneeUserId,
    'rotationUserIds': rotationUserIds,
  };
}

class MemberChoreStats {
  const MemberChoreStats({
    required this.userId,
    required this.displayName,
    required this.color,
    required this.completed,
    required this.points,
  });

  final String userId;
  final String displayName;
  final String color;
  final int completed;
  final int points;

  factory MemberChoreStats.fromJson(Map<String, dynamic> json) => MemberChoreStats(
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
    color: json['color'] as String,
    completed: json['completed'] as int,
    points: json['points'] as int,
  );
}

DateTime? _instant(Object? value) => value == null ? null : DateTime.parse(value as String).toLocal();

DateTime? _day(Object? value) => value == null ? null : DateTime.parse(value as String);

/// Um evento do calendário da casa. Os de dia inteiro têm [startDate] e [endDate] (dias, inclusive);
/// os outros têm [startsAt] e [endsAt], na hora local do dispositivo.
class CalendarEvent {
  const CalendarEvent({
    required this.id,
    required this.householdId,
    required this.title,
    required this.notes,
    required this.location,
    required this.isAllDay,
    required this.startsAt,
    required this.endsAt,
    required this.startDate,
    required this.endDate,
    required this.recurrence,
    required this.participantUserIds,
    required this.dropOffUserId,
    required this.pickUpUserId,
    this.seriesId,
    this.originalDate,
    required this.updatedAt,
  });

  final String id;
  final String householdId;
  final String title;
  final String? notes;
  final String? location;
  final bool isAllDay;
  final DateTime? startsAt;
  final DateTime? endsAt;
  final DateTime? startDate;
  final DateTime? endDate;

  /// RRULE canónica (ex.: "FREQ=WEEKLY;BYDAY=TU,TH"); null se não se repete. Ver [Recurrence].
  final String? recurrence;
  final List<String> participantUserIds;

  /// Boleias: quem leva e quem vai buscar.
  final String? dropOffUserId;
  final String? pickUpUserId;

  /// Numa alteração a um só dia de uma série: a série e o dia que substitui.
  final String? seriesId;
  final DateTime? originalDate;
  final DateTime updatedAt;

  bool get isRecurring => recurrence != null;

  factory CalendarEvent.fromJson(Map<String, dynamic> json) => CalendarEvent(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    title: json['title'] as String,
    notes: json['notes'] as String?,
    location: json['location'] as String?,
    isAllDay: json['isAllDay'] as bool,
    startsAt: _instant(json['startsAt']),
    endsAt: _instant(json['endsAt']),
    startDate: _day(json['startDate']),
    endDate: _day(json['endDate']),
    recurrence: json['recurrence'] as String?,
    participantUserIds: (json['participantUserIds'] as List).cast<String>(),
    dropOffUserId: json['dropOffUserId'] as String?,
    pickUpUserId: json['pickUpUserId'] as String?,
    seriesId: json['seriesId'] as String?,
    originalDate: _day(json['originalDate']),
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );
}

/// Uma ocorrência de um evento no intervalo pedido. [conflictUserIds] são os membros ocupados com outro
/// evento à mesma hora, ou quando têm de levar ou ir buscar alguém.
class CalendarOccurrence {
  const CalendarOccurrence({
    required this.eventId,
    required this.occurrenceDate,
    required this.startsAt,
    required this.endsAt,
    required this.startDate,
    required this.endDate,
    required this.conflictUserIds,
  });

  final String eventId;

  /// O dia que identifica esta ocorrência dentro da série (para alterar ou apagar só esta).
  final DateTime occurrenceDate;
  final DateTime? startsAt;
  final DateTime? endsAt;
  final DateTime? startDate;
  final DateTime? endDate;
  final List<String> conflictUserIds;

  bool get isAllDay => startDate != null;

  /// Se ocupa alguma parte de [day] (hora local).
  bool occursOn(DateTime day) {
    final start = DateTime(day.year, day.month, day.day);
    final end = DateTime(day.year, day.month, day.day + 1);
    if (isAllDay) return !startDate!.isAfter(start) && !endDate!.isBefore(start);
    return startsAt!.isBefore(end) && endsAt!.isAfter(start);
  }

  factory CalendarOccurrence.fromJson(Map<String, dynamic> json) => CalendarOccurrence(
    eventId: json['eventId'] as String,
    occurrenceDate: DateTime.parse(json['occurrenceDate'] as String),
    startsAt: _instant(json['startsAt']),
    endsAt: _instant(json['endsAt']),
    startDate: _day(json['startDate']),
    endDate: _day(json['endDate']),
    conflictUserIds: (json['conflictUserIds'] as List).cast<String>(),
  );
}

class Holiday {
  const Holiday({required this.date, required this.name});

  final DateTime date;
  final String name;

  factory Holiday.fromJson(Map<String, dynamic> json) =>
      Holiday(date: DateTime.parse(json['date'] as String), name: json['name'] as String);
}

/// O calendário de um intervalo de dias: eventos, as suas ocorrências e os feriados nacionais.
class CalendarRange {
  const CalendarRange({required this.events, required this.occurrences, required this.holidays});

  final Map<String, CalendarEvent> events;
  final List<CalendarOccurrence> occurrences;
  final List<Holiday> holidays;

  List<CalendarOccurrence> on(DateTime day) =>
      occurrences.where((o) => o.occursOn(day) && events.containsKey(o.eventId)).toList();

  /// Membros indisponíveis para alguma ocorrência de [eventId].
  Set<String> conflictsFor(String eventId) =>
      occurrences.where((o) => o.eventId == eventId).expand((o) => o.conflictUserIds).toSet();

  Holiday? holidayOn(DateTime day) {
    for (final h in holidays) {
      if (h.date.year == day.year && h.date.month == day.month && h.date.day == day.day) return h;
    }
    return null;
  }

  factory CalendarRange.fromJson(Map<String, dynamic> json) {
    final events = [for (final e in json['events'] as List) CalendarEvent.fromJson(e as Map<String, dynamic>)];
    final occurrences = json['occurrences'] as List;
    final holidays = json['holidays'] as List;
    return CalendarRange(
      events: {for (final e in events) e.id: e},
      occurrences: [for (final o in occurrences) CalendarOccurrence.fromJson(o as Map<String, dynamic>)],
      holidays: [for (final h in holidays) Holiday.fromJson(h as Map<String, dynamic>)],
    );
  }
}

/// Dados para criar ou editar um evento. Nos de dia inteiro só contam os dias de [start] e [end] (inclusive).
class CalendarEventDraft {
  const CalendarEventDraft({
    required this.title,
    required this.start,
    required this.end,
    this.isAllDay = false,
    this.notes,
    this.location,
    this.recurrence,
    this.participantUserIds = const [],
    this.dropOffUserId,
    this.pickUpUserId,
  });

  final String title;
  final DateTime start;
  final DateTime end;
  final bool isAllDay;
  final String? notes;
  final String? location;
  final String? recurrence;
  final List<String> participantUserIds;
  final String? dropOffUserId;
  final String? pickUpUserId;

  CalendarEventDraft copyWith({DateTime? start, DateTime? end}) => CalendarEventDraft(
    title: title,
    start: start ?? this.start,
    end: end ?? this.end,
    isAllDay: isAllDay,
    notes: notes,
    location: location,
    recurrence: recurrence,
    participantUserIds: participantUserIds,
    dropOffUserId: dropOffUserId,
    pickUpUserId: pickUpUserId,
  );

  factory CalendarEventDraft.from(CalendarEvent event) => CalendarEventDraft(
    title: event.title,
    start: (event.isAllDay ? event.startDate : event.startsAt)!,
    end: (event.isAllDay ? event.endDate : event.endsAt)!,
    isAllDay: event.isAllDay,
    notes: event.notes,
    location: event.location,
    recurrence: event.recurrence,
    participantUserIds: event.participantUserIds,
    dropOffUserId: event.dropOffUserId,
    pickUpUserId: event.pickUpUserId,
  );

  Map<String, dynamic> toJson() => {
    'title': title,
    if (notes != null && notes!.isNotEmpty) 'notes': notes,
    if (location != null && location!.isNotEmpty) 'location': location,
    'isAllDay': isAllDay,
    if (isAllDay) 'startDate': apiDate(start),
    if (isAllDay) 'endDate': apiDate(end),
    if (!isAllDay) 'startsAt': start.toUtc().toIso8601String(),
    if (!isAllDay) 'endsAt': end.toUtc().toIso8601String(),
    if (recurrence != null) 'recurrence': recurrence,
    'participantUserIds': participantUserIds,
    if (dropOffUserId != null) 'dropOffUserId': dropOffUserId,
    if (pickUpUserId != null) 'pickUpUserId': pickUpUserId,
  };
}

/// Endereço pessoal (iCalendar) para subscrever o calendário da casa noutras apps.
class CalendarFeed {
  const CalendarFeed({required this.url, required this.webcalUrl});

  final String url;
  final String webcalUrl;

  factory CalendarFeed.fromJson(Map<String, dynamic> json) =>
      CalendarFeed(url: json['url'] as String, webcalUrl: json['webcalUrl'] as String);
}

enum RecurrenceFrequency {
  daily('DAILY', 'Todos os dias', 'dias'),
  weekly('WEEKLY', 'Todas as semanas', 'semanas'),
  monthly('MONTHLY', 'Todos os meses', 'meses'),
  yearly('YEARLY', 'Todos os anos', 'anos');

  const RecurrenceFrequency(this.apiValue, this.label, this.unit);

  final String apiValue;
  final String label;
  final String unit;
}

/// Repetição de um evento: o subconjunto de RRULE (RFC 5545) que a API aceita.
class Recurrence {
  const Recurrence(this.frequency, {this.interval = 1, this.weekdays = const [], this.count, this.until});

  final RecurrenceFrequency frequency;
  final int interval;

  /// Dias da semana, de [DateTime.monday] a [DateTime.sunday]; só nas repetições semanais.
  final List<int> weekdays;
  final int? count;

  /// Último dia (inclusive).
  final DateTime? until;

  static const _dayCodes = ['MO', 'TU', 'WE', 'TH', 'FR', 'SA', 'SU'];

  /// Lê a RRULE que a API guarda; null se não houver ou não for reconhecida.
  static Recurrence? parse(String? rule) {
    if (rule == null || rule.isEmpty) return null;
    RecurrenceFrequency? frequency;
    var interval = 1;
    var weekdays = <int>[];
    int? count;
    DateTime? until;

    for (final part in rule.split(';')) {
      final separator = part.indexOf('=');
      if (separator < 0) return null;
      final value = part.substring(separator + 1);
      switch (part.substring(0, separator)) {
        case 'FREQ':
          for (final f in RecurrenceFrequency.values) {
            if (f.apiValue == value) frequency = f;
          }
        case 'INTERVAL':
          interval = int.tryParse(value) ?? 1;
        case 'BYDAY':
          weekdays = value.split(',').map(_dayCodes.indexOf).where((i) => i >= 0).map((i) => i + 1).toList();
        case 'COUNT':
          count = int.tryParse(value);
        case 'UNTIL':
          if (value.length >= 8) until = DateTime.tryParse(value.substring(0, 8));
      }
    }

    if (frequency == null) return null;
    return Recurrence(frequency, interval: interval, weekdays: weekdays, count: count, until: until);
  }

  /// A RRULE para enviar à API (ex.: "FREQ=WEEKLY;BYDAY=TU,TH;UNTIL=20261231").
  String toRule() {
    final days = [...weekdays]..sort();
    final byDay = frequency == RecurrenceFrequency.weekly ? days.map((d) => _dayCodes[d - 1]).join(',') : '';
    return [
      'FREQ=${frequency.apiValue}',
      if (interval != 1) 'INTERVAL=$interval',
      if (byDay.isNotEmpty) 'BYDAY=$byDay',
      if (count != null) 'COUNT=$count',
      if (until != null) 'UNTIL=${apiDate(until!).replaceAll('-', '')}',
    ].join(';');
  }
}

enum RedemptionStatus {
  pending('Pending', 'À espera de aprovação'),
  approved('Approved', 'Aprovado'),
  rejected('Rejected', 'Recusado');

  const RedemptionStatus(this.apiValue, this.label);

  final String apiValue;
  final String label;

  static RedemptionStatus fromApi(String value) => values.firstWhere((s) => s.apiValue == value, orElse: () => pending);
}

/// Uma recompensa que se troca por pontos das tarefas.
class Reward {
  const Reward({
    required this.id,
    required this.householdId,
    required this.title,
    required this.description,
    required this.cost,
    required this.updatedAt,
  });

  final String id;
  final String householdId;
  final String title;
  final String? description;
  final int cost;
  final DateTime updatedAt;

  factory Reward.fromJson(Map<String, dynamic> json) => Reward(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    title: json['title'] as String,
    description: json['description'] as String?,
    cost: json['cost'] as int,
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );
}

/// Dados para criar ou editar uma recompensa.
class RewardDraft {
  const RewardDraft({required this.title, this.description, required this.cost});

  final String title;
  final String? description;
  final int cost;

  Map<String, dynamic> toJson() => {
    'title': title,
    if (description != null && description!.isNotEmpty) 'description': description,
    'cost': cost,
  };
}

/// Um pedido de troca de pontos por uma recompensa. Guarda o nome e o custo de quando foi feito.
class RewardRedemption {
  const RewardRedemption({
    required this.id,
    required this.householdId,
    required this.rewardId,
    required this.userId,
    required this.rewardTitle,
    required this.points,
    required this.status,
    required this.requestedAt,
    required this.decidedAt,
  });

  final String id;
  final String householdId;
  final String? rewardId;
  final String userId;
  final String rewardTitle;
  final int points;
  final RedemptionStatus status;
  final DateTime requestedAt;
  final DateTime? decidedAt;

  factory RewardRedemption.fromJson(Map<String, dynamic> json) => RewardRedemption(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    rewardId: json['rewardId'] as String?,
    userId: json['userId'] as String,
    rewardTitle: json['rewardTitle'] as String,
    points: json['points'] as int,
    status: RedemptionStatus.fromApi(json['status'] as String),
    requestedAt: DateTime.parse(json['requestedAt'] as String).toLocal(),
    decidedAt: _instant(json['decidedAt']),
  );
}

/// Saldo de um membro: ganhos nas tarefas, já trocados, reservados (pedidos à espera) e disponíveis.
class MemberPoints {
  const MemberPoints({
    required this.userId,
    required this.displayName,
    required this.color,
    required this.earned,
    required this.spent,
    required this.reserved,
    required this.available,
  });

  final String userId;
  final String displayName;
  final String color;
  final int earned;
  final int spent;
  final int reserved;
  final int available;

  factory MemberPoints.fromJson(Map<String, dynamic> json) => MemberPoints(
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
    color: json['color'] as String,
    earned: json['earned'] as int,
    spent: json['spent'] as int,
    reserved: json['reserved'] as int,
    available: json['available'] as int,
  );
}

/// As recompensas da casa, os pedidos recentes (pendentes primeiro) e o saldo de cada membro.
class RewardsOverview {
  const RewardsOverview({required this.rewards, required this.redemptions, required this.points});

  final List<Reward> rewards;
  final List<RewardRedemption> redemptions;
  final List<MemberPoints> points;

  MemberPoints? pointsOf(String? userId) {
    for (final p in points) {
      if (p.userId == userId) return p;
    }
    return null;
  }

  factory RewardsOverview.fromJson(Map<String, dynamic> json) {
    final rewards = json['rewards'] as List;
    final redemptions = json['redemptions'] as List;
    final points = json['points'] as List;
    return RewardsOverview(
      rewards: [for (final r in rewards) Reward.fromJson(r as Map<String, dynamic>)],
      redemptions: [for (final r in redemptions) RewardRedemption.fromJson(r as Map<String, dynamic>)],
      points: [for (final p in points) MemberPoints.fromJson(p as Map<String, dynamic>)],
    );
  }
}
