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
  const HouseholdDetails({required this.id, required this.name, required this.myRole, required this.members});

  final String id;
  final String name;
  final HouseholdRole myRole;
  final List<Member> members;

  factory HouseholdDetails.fromJson(Map<String, dynamic> json) => HouseholdDetails(
    id: json['id'] as String,
    name: json['name'] as String,
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
