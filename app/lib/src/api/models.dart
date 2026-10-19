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

/// As áreas da app cujo acesso o administrador escolhe por papel.
enum HouseholdModule {
  shopping('Shopping', 'Compras', 'adiciona e marca artigos'),
  chores('Chores', 'Tarefas', 'conclui as suas tarefas'),
  calendar('Calendar', 'Calendário', null),
  rewards('Rewards', 'Recompensas', 'pede recompensas');

  const HouseholdModule(this.apiValue, this.label, this.participateMeans);

  final String apiValue;
  final String label;

  /// O que "usa" quer dizer nesta área. No calendário não há meio-termo: ou só se vê, ou gere-se.
  final String? participateMeans;

  List<ModuleAccess> get levels => [
    for (final a in ModuleAccess.values)
      if (participateMeans != null || a != ModuleAccess.participate) a,
  ];

  static HouseholdModule? fromApi(String value) => values.where((m) => m.apiValue == value).firstOrNull;
}

/// O que um papel pode fazer numa área, do menos para o mais.
enum ModuleAccess {
  none('None', 'Não aparece'),
  view('View', 'Só vê'),
  participate('Participate', 'Usa'),
  manage('Manage', 'Gere tudo');

  const ModuleAccess(this.apiValue, this.label);

  final String apiValue;
  final String label;

  bool get canView => index >= view.index;
  bool get canParticipate => index >= participate.index;
  bool get canManage => this == manage;

  static ModuleAccess fromApi(String value) => values.firstWhere((a) => a.apiValue == value, orElse: () => none);

  /// O habitual (igual ao do servidor): adultos gerem tudo; crianças e convidados usam, e no calendário só veem.
  static ModuleAccess defaultFor(HouseholdRole role, HouseholdModule module) => role.canManageContent
      ? manage
      : module == HouseholdModule.calendar
      ? view
      : participate;

  static Map<HouseholdModule, ModuleAccess> parse(Object? json, HouseholdRole role) => {
    for (final module in HouseholdModule.values)
      module: json is Map && json[module.apiValue] is String
          ? fromApi(json[module.apiValue] as String)
          : defaultFor(role, module),
  };

  static Map<String, String> toJson(Map<HouseholdModule, ModuleAccess> access) => {
    for (final e in access.entries) e.key.apiValue: e.value.apiValue,
  };
}

/// Quem está a usar a app. Um perfil de criança não tem email: entra com o código da família e um PIN.
class User {
  const User({required this.id, required this.email, required this.displayName, this.isChildProfile = false});

  final String id;
  final String? email;
  final String displayName;
  final bool isChildProfile;

  factory User.fromJson(Map<String, dynamic> json) => User(
    id: json['id'] as String,
    email: json['email'] as String?,
    displayName: json['displayName'] as String,
    isChildProfile: json['isChildProfile'] as bool? ?? false,
  );
}

/// Sessão iniciada: o token de acesso (dura pouco) e o refresh token, para pedir um par novo.
class AuthResult {
  const AuthResult({required this.accessToken, required this.user, this.expiresAt, this.refreshToken});

  final String accessToken;
  final User user;
  final DateTime? expiresAt;
  final String? refreshToken;

  factory AuthResult.fromJson(Map<String, dynamic> json) => AuthResult(
    accessToken: json['accessToken'] as String,
    user: User.fromJson(json['user'] as Map<String, dynamic>),
    expiresAt: json['expiresAt'] == null ? null : DateTime.parse(json['expiresAt'] as String),
    refreshToken: json['refreshToken'] as String?,
  );
}

/// Uma sessão iniciada num dispositivo. [device] é android, ios, windows, macos, linux ou web.
class DeviceSession {
  const DeviceSession({
    required this.id,
    required this.device,
    required this.startedAt,
    required this.lastActiveAt,
    required this.isCurrent,
  });

  final String id;
  final String? device;
  final DateTime startedAt;
  final DateTime lastActiveAt;

  /// A sessão deste dispositivo.
  final bool isCurrent;

  factory DeviceSession.fromJson(Map<String, dynamic> json) => DeviceSession(
    id: json['id'] as String,
    device: json['device'] as String?,
    startedAt: DateTime.parse(json['startedAt'] as String).toLocal(),
    lastActiveAt: DateTime.parse(json['lastActiveAt'] as String).toLocal(),
    isCurrent: json['isCurrent'] as bool,
  );
}

class HouseholdSummary {
  const HouseholdSummary({required this.id, required this.name, required this.role, this.access = const {}});

  final String id;
  final String name;
  final HouseholdRole role;

  /// O que posso fazer em cada área desta casa (o que faltar é o habitual do meu papel).
  final Map<HouseholdModule, ModuleAccess> access;

  ModuleAccess accessTo(HouseholdModule module) => access[module] ?? ModuleAccess.defaultFor(role, module);

  factory HouseholdSummary.fromJson(Map<String, dynamic> json) {
    final role = HouseholdRole.fromApi(json['role'] as String);
    return HouseholdSummary(
      id: json['id'] as String,
      name: json['name'] as String,
      role: role,
      access: ModuleAccess.parse(json['access'], role),
    );
  }
}

/// O acesso de um papel a cada área.
class RolePermissions {
  const RolePermissions({required this.role, required this.access});

  final HouseholdRole role;
  final Map<HouseholdModule, ModuleAccess> access;

  factory RolePermissions.fromJson(Map<String, dynamic> json) {
    final role = HouseholdRole.fromApi(json['role'] as String);
    return RolePermissions(role: role, access: ModuleAccess.parse(json['access'], role));
  }

  Map<String, dynamic> toJson() => {'role': role.apiValue, 'access': ModuleAccess.toJson(access)};
}

/// O que cada papel (adulto, criança, convidado) pode fazer nesta casa, e o habitual. O administrador tem tudo.
class HouseholdPermissions {
  const HouseholdPermissions({required this.roles, required this.defaults});

  final List<RolePermissions> roles;
  final List<RolePermissions> defaults;

  factory HouseholdPermissions.fromJson(Map<String, dynamic> json) => HouseholdPermissions(
    roles: [for (final r in json['roles'] as List) RolePermissions.fromJson(r as Map<String, dynamic>)],
    defaults: [for (final r in json['defaults'] as List) RolePermissions.fromJson(r as Map<String, dynamic>)],
  );
}

class Me {
  const Me({required this.user, required this.households, this.isSupport = false});

  final User user;
  final List<HouseholdSummary> households;

  /// Esta conta mantém o Kubico e lê as mensagens enviadas da ajuda.
  final bool isSupport;

  factory Me.fromJson(Map<String, dynamic> json) => Me(
    user: User.fromJson(json['user'] as Map<String, dynamic>),
    households: (json['households'] as List).map((h) => HouseholdSummary.fromJson(h as Map<String, dynamic>)).toList(),
    isSupport: json['isSupport'] as bool? ?? false,
  );
}

enum FeedbackKind {
  problem('Problem'),
  idea('Idea');

  const FeedbackKind(this.apiValue);

  final String apiValue;

  static FeedbackKind fromApi(String value) => values.firstWhere((k) => k.apiValue == value, orElse: () => problem);
}

/// Uma mensagem enviada da ajuda (só a vê quem mantém o Kubico).
class FeedbackItem {
  const FeedbackItem({
    required this.id,
    required this.kind,
    required this.message,
    required this.createdAt,
    this.device,
    this.userName,
    this.userEmail,
  });

  final String id;
  final FeedbackKind kind;
  final String message;
  final DateTime createdAt;
  final String? device;
  final String? userName;
  final String? userEmail;

  factory FeedbackItem.fromJson(Map<String, dynamic> json) => FeedbackItem(
    id: json['id'] as String,
    kind: FeedbackKind.fromApi(json['kind'] as String),
    message: json['message'] as String,
    createdAt: _instant(json['createdAt'])!,
    device: json['device'] as String?,
    userName: json['userName'] as String?,
    userEmail: json['userEmail'] as String?,
  );
}

class Member {
  const Member({
    required this.userId,
    required this.displayName,
    required this.role,
    required this.color,
    this.isChildProfile = false,
    this.birthDate,
  });

  final String userId;
  final String displayName;
  final HouseholdRole role;
  final String color;

  /// Perfil de criança: sem conta própria, entra com o código da família e um PIN.
  final bool isChildProfile;
  final DateTime? birthDate;

  factory Member.fromJson(Map<String, dynamic> json) => Member(
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
    role: HouseholdRole.fromApi(json['role'] as String),
    color: json['color'] as String,
    isChildProfile: json['isChildProfile'] as bool? ?? false,
    birthDate: _day(json['birthDate']),
  );
}

/// Alguém está fora entre dois dias (inclusive): sai das rotações das tarefas enquanto está fora.
class MemberAbsence {
  const MemberAbsence({
    required this.id,
    required this.userId,
    required this.startDate,
    required this.endDate,
    this.reason,
  });

  final String id;
  final String userId;
  final DateTime startDate;
  final DateTime endDate;
  final String? reason;

  bool covers(DateTime day) {
    final d = DateTime(day.year, day.month, day.day);
    return !d.isBefore(startDate) && !d.isAfter(endDate);
  }

  factory MemberAbsence.fromJson(Map<String, dynamic> json) => MemberAbsence(
    id: json['id'] as String,
    userId: json['userId'] as String,
    startDate: _day(json['startDate'])!,
    endDate: _day(json['endDate'])!,
    reason: json['reason'] as String?,
  );
}

/// Um perfil de criança para escolher no ecrã de entrada (depois do código da família).
class ChildProfileOption {
  const ChildProfileOption({required this.userId, required this.displayName, required this.color});

  final String userId;
  final String displayName;
  final String color;

  factory ChildProfileOption.fromJson(Map<String, dynamic> json) => ChildProfileOption(
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
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

/// O que um convite diz antes de ser aceite: que casa é, com que papel, e se já lá estás.
class InvitePreview {
  const InvitePreview({
    required this.householdId,
    required this.householdName,
    required this.role,
    required this.alreadyMember,
  });

  final String householdId;
  final String householdName;
  final HouseholdRole role;
  final bool alreadyMember;

  factory InvitePreview.fromJson(Map<String, dynamic> json) => InvitePreview(
    householdId: json['householdId'] as String,
    householdName: json['householdName'] as String,
    role: HouseholdRole.fromApi(json['role'] as String),
    alreadyMember: json['alreadyMember'] as bool,
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
    this.barcode,
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

  /// O código de barras, quando o artigo foi lido com a câmara.
  final String? barcode;

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
    barcode: barcode,
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
    'barcode': ?barcode,
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
    barcode: json['barcode'] as String?,
  );
}

/// Um produto lido pelo código de barras. [fromHousehold]: é o nome que esta casa lhe deu da última vez.
class Product {
  const Product({
    required this.barcode,
    required this.name,
    this.category,
    this.brand,
    this.size,
    this.fromHousehold = false,
  });

  final String barcode;
  final String name;
  final String? category;
  final String? brand;
  final String? size;
  final bool fromHousehold;

  factory Product.fromJson(Map<String, dynamic> json) => Product(
    barcode: json['barcode'] as String,
    name: json['name'] as String,
    category: json['category'] as String?,
    brand: json['brand'] as String?,
    size: json['size'] as String?,
    fromHousehold: json['fromHousehold'] as bool? ?? false,
  );
}

/// EAN-8, UPC-A, EAN-13 e ITF-14 com o dígito de controlo certo (igual ao servidor): um código mal lido não serve.
bool isValidBarcode(String code) {
  if (!const [8, 12, 13, 14].contains(code.length) || !RegExp(r'^\d+$').hasMatch(code)) return false;
  var sum = 0;
  for (var i = code.length - 2; i >= 0; i--) {
    sum += int.parse(code[i]) * ((code.length - 2 - i).isEven ? 3 : 1);
  }
  return (10 - sum % 10) % 10 == int.parse(code[code.length - 1]);
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
    this.timeOfDay = ChoreTimeOfDay.any,
    this.steps = const [],
    this.doneSteps = const [],
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

  /// Manhã ou noite (as rotinas); "qualquer altura" nas outras tarefas.
  final ChoreTimeOfDay timeOfDay;

  /// Os passos de uma rotina; vazia numa tarefa simples.
  final List<String> steps;

  /// Os passos já feitos desta vez (posições em [steps]).
  final List<int> doneSteps;

  bool get rotates => rotationUserIds.isNotEmpty;
  bool get isRoutine => steps.isNotEmpty;

  Chore copyWith({List<int>? doneSteps}) => Chore(
    id: id,
    householdId: householdId,
    title: title,
    notes: notes,
    points: points,
    dueDate: dueDate,
    recurrence: recurrence,
    assigneeUserId: assigneeUserId,
    rotationUserIds: rotationUserIds,
    isDone: isDone,
    updatedAt: updatedAt,
    timeOfDay: timeOfDay,
    steps: steps,
    doneSteps: doneSteps ?? this.doneSteps,
  );

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
    timeOfDay: ChoreTimeOfDay.fromApi(json['timeOfDay'] as String?),
    steps: (json['steps'] as List?)?.cast<String>() ?? const [],
    doneSteps: (json['doneSteps'] as List?)?.cast<int>() ?? const [],
  );
}

/// Em que altura do dia se faz uma tarefa.
enum ChoreTimeOfDay {
  morning('Morning', 'Manhã'),
  any('Any', 'Qualquer altura'),
  evening('Evening', 'Noite');

  const ChoreTimeOfDay(this.apiValue, this.label);

  final String apiValue;
  final String label;

  static ChoreTimeOfDay fromApi(String? value) => values.firstWhere((t) => t.apiValue == value, orElse: () => any);
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
    this.timeOfDay = ChoreTimeOfDay.any,
    this.steps = const [],
  });

  final String title;
  final String? notes;
  final int points;
  final DateTime? dueDate;
  final ChoreRecurrence recurrence;
  final String? assigneeUserId;
  final List<String> rotationUserIds;
  final ChoreTimeOfDay timeOfDay;
  final List<String> steps;

  factory ChoreDraft.from(Chore chore) => ChoreDraft(
    title: chore.title,
    notes: chore.notes,
    points: chore.points,
    dueDate: chore.dueDate,
    recurrence: chore.recurrence,
    assigneeUserId: chore.assigneeUserId,
    rotationUserIds: chore.rotationUserIds,
    timeOfDay: chore.timeOfDay,
    steps: chore.steps,
  );

  Map<String, dynamic> toJson() => {
    'title': title,
    if (notes != null && notes!.isNotEmpty) 'notes': notes,
    'points': points,
    if (dueDate != null) 'dueDate': apiDate(dueDate!),
    'recurrence': recurrence.apiValue,
    if (assigneeUserId != null) 'assigneeUserId': assigneeUserId,
    'rotationUserIds': rotationUserIds,
    'timeOfDay': timeOfDay.apiValue,
    'steps': steps,
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

/// Um serviço de calendário com que o Kubico sincroniza nos dois sentidos. [name] é o que vai no endereço da API.
enum CalendarProvider {
  google('Google', 'Google Calendar', 'conta Google'),
  microsoft('Microsoft', 'Outlook', 'conta Microsoft');

  const CalendarProvider(this.apiValue, this.label, this.account);

  final String apiValue;
  final String label;

  /// Como se chama a conta ("Ligar a conta Google").
  final String account;

  static CalendarProvider? fromApi(String value) => values.where((p) => p.apiValue == value).firstOrNull;
}

/// A ligação deste membro a uma conta Google ou Microsoft. [lastError] já vem pronto a mostrar.
class CalendarConnection {
  const CalendarConnection({
    required this.id,
    required this.householdId,
    required this.provider,
    required this.accountEmail,
    required this.needsReconnect,
    required this.lastSyncedAt,
    required this.lastError,
  });

  final String id;
  final String householdId;
  final CalendarProvider provider;
  final String accountEmail;

  /// O acesso à conta expirou ou foi retirado: é preciso voltar a ligar.
  final bool needsReconnect;
  final DateTime? lastSyncedAt;
  final String? lastError;

  factory CalendarConnection.fromJson(Map<String, dynamic> json) => CalendarConnection(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    provider: CalendarProvider.fromApi(json['provider'] as String) ?? CalendarProvider.google,
    accountEmail: json['accountEmail'] as String,
    needsReconnect: json['status'] == 'NeedsReconnect',
    lastSyncedAt: _instant(json['lastSyncedAt']),
    lastError: json['lastError'] as String?,
  );
}

/// Que serviços o servidor tem configurados, se quem pede pode ligar contas (só adultos) e as suas ligações.
class CalendarSyncOverview {
  const CalendarSyncOverview({required this.canConnect, required this.available, required this.connections});

  final bool canConnect;
  final Set<CalendarProvider> available;
  final List<CalendarConnection> connections;

  CalendarConnection? connectionFor(CalendarProvider provider) =>
      connections.where((c) => c.provider == provider).firstOrNull;

  factory CalendarSyncOverview.fromJson(Map<String, dynamic> json) => CalendarSyncOverview(
    canConnect: json['canConnect'] as bool,
    available: {
      for (final p in json['providers'] as List)
        if ((p as Map<String, dynamic>)['available'] == true) ?CalendarProvider.fromApi(p['provider'] as String),
    },
    connections: [for (final c in json['connections'] as List) CalendarConnection.fromJson(c as Map<String, dynamic>)],
  );
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

/// Um aviso pessoal (de qualquer casa): uma tarefa ou um evento que passou a ser teu, um pedido de recompensa…
class AppNotification {
  const AppNotification({
    required this.id,
    required this.householdId,
    required this.kind,
    required this.title,
    required this.body,
    required this.entityId,
    required this.createdAt,
    required this.isRead,
  });

  final String id;
  final String householdId;

  /// ChoreAssigned, EventAssigned, RideAssigned, RewardRequested ou RewardDecided.
  final String kind;
  final String title;
  final String? body;
  final String? entityId;
  final DateTime createdAt;
  final bool isRead;

  AppNotification read() => AppNotification(
    id: id,
    householdId: householdId,
    kind: kind,
    title: title,
    body: body,
    entityId: entityId,
    createdAt: createdAt,
    isRead: true,
  );

  factory AppNotification.fromJson(Map<String, dynamic> json) => AppNotification(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    kind: json['kind'] as String,
    title: json['title'] as String,
    body: json['body'] as String?,
    entityId: json['entityId'] as String?,
    createdAt: DateTime.parse(json['createdAt'] as String).toLocal(),
    isRead: json['isRead'] as bool,
  );
}

enum NotificationDelivery {
  immediate('Immediate', 'Logo que acontece'),
  dailyDigest('DailyDigest', 'Um resumo por dia'),
  inAppOnly('InAppOnly', 'Só aqui na app');

  const NotificationDelivery(this.apiValue, this.label);

  final String apiValue;
  final String label;

  static NotificationDelivery fromApi(String value) =>
      values.firstWhere((d) => d.apiValue == value, orElse: () => immediate);
}

/// "19:00:00" → 19:00 (as horas vêm do servidor como TimeOnly).
({int hour, int minute}) _time(String value) {
  final parts = value.split(':');
  return (hour: int.parse(parts[0]), minute: int.parse(parts[1]));
}

String _timeJson(({int hour, int minute}) t) =>
    '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}:00';

/// Como cada membro quer receber os avisos no telemóvel, e as horas de silêncio.
class NotificationPreferences {
  const NotificationPreferences({
    required this.delivery,
    required this.digestAt,
    required this.quietFrom,
    required this.quietUntil,
    required this.timeZoneId,
  });

  final NotificationDelivery delivery;
  final ({int hour, int minute}) digestAt;
  final ({int hour, int minute})? quietFrom;
  final ({int hour, int minute})? quietUntil;
  final String timeZoneId;

  factory NotificationPreferences.fromJson(Map<String, dynamic> json) => NotificationPreferences(
    delivery: NotificationDelivery.fromApi(json['delivery'] as String),
    digestAt: _time(json['digestAt'] as String),
    quietFrom: json['quietFrom'] == null ? null : _time(json['quietFrom'] as String),
    quietUntil: json['quietUntil'] == null ? null : _time(json['quietUntil'] as String),
    timeZoneId: json['timeZoneId'] as String,
  );

  Map<String, dynamic> toJson() => {
    'delivery': delivery.apiValue,
    'digestAt': _timeJson(digestAt),
    'quietFrom': quietFrom == null ? null : _timeJson(quietFrom!),
    'quietUntil': quietUntil == null ? null : _timeJson(quietUntil!),
    'timeZoneId': timeZoneId,
  };
}
