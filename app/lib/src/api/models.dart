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

String _date(DateTime d) =>
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
    if (dueDate != null) 'dueDate': _date(dueDate!),
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
