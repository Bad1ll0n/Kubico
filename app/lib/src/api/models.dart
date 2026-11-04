/// Modelos que espelham os DTOs da API (backend/src/Kubico.Api).
library;

import '../l10n/strings.dart';

enum HouseholdRole {
  admin('Admin', 'Administrador'),
  adult('Adult', 'Adulto'),
  child('Child', 'Criança'),
  guest('Guest', 'Convidado');

  const HouseholdRole(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  bool get canManageContent => this == admin || this == adult;
  bool get canManageMembers => this == admin;

  /// Os papéis com que se convida: para administrador não há convites (passa-se a administração depois).
  static const invitable = [adult, child, guest];

  /// Um valor desconhecido (de uma versão nova da API, ou estragado) fica como o papel com menos acesso.
  static HouseholdRole fromApi(Object? value) => values.firstWhere((r) => r.apiValue == value, orElse: () => guest);
}

/// As áreas da app cujo acesso o administrador escolhe por papel.
enum HouseholdModule {
  shopping('Shopping', 'Compras', 'adiciona e marca artigos'),
  chores('Chores', 'Tarefas', 'conclui as suas tarefas'),
  calendar('Calendar', 'Calendário', null),
  rewards('Rewards', 'Recompensas', 'pede recompensas'),
  meals('Meals', 'Refeições', 'planeia as refeições'),
  expenses('Expenses', 'Despesas', 'regista as suas despesas'),
  home('Home', 'Casa', 'deixa recados e guarda os seus documentos');

  const HouseholdModule(this.apiValue, this._label, this._participateMeans);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  final String? _participateMeans;

  /// O que "usa" quer dizer nesta área. No calendário não há meio-termo: ou só se vê, ou gere-se.
  String? get participateMeans => _participateMeans?.tr;

  List<ModuleAccess> get levels => [
    for (final a in ModuleAccess.values)
      if (participateMeans != null || a != ModuleAccess.participate) a,
  ];

  static HouseholdModule? fromApi(Object? value) => values.where((m) => m.apiValue == value).firstOrNull;
}

/// O que um papel pode fazer numa área, do menos para o mais.
enum ModuleAccess {
  none('None', 'Não aparece'),
  view('View', 'Só vê'),
  participate('Participate', 'Usa'),
  manage('Manage', 'Gere tudo');

  const ModuleAccess(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  bool get canView => index >= view.index;
  bool get canParticipate => index >= participate.index;
  bool get canManage => this == manage;

  static ModuleAccess fromApi(Object? value) => values.firstWhere((a) => a.apiValue == value, orElse: () => none);

  /// O habitual (igual ao do servidor): adultos gerem tudo; crianças e convidados usam, e no calendário só veem.
  /// As despesas não aparecem às crianças (é dinheiro dos adultos).
  static ModuleAccess defaultFor(HouseholdRole role, HouseholdModule module) => role.canManageContent
      ? manage
      : role == HouseholdRole.child && module == HouseholdModule.expenses
      ? none
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
  const User({
    required this.id,
    required this.email,
    required this.displayName,
    this.isChildProfile = false,
    this.language = 'pt',
  });

  final String id;
  final String? email;
  final String displayName;
  final bool isChildProfile;

  /// A língua em que recebe os avisos ("pt" ou "en").
  final String language;

  factory User.fromJson(Map<String, dynamic> json) => User(
    id: json['id'] as String,
    email: json['email'] as String?,
    displayName: json['displayName'] as String,
    isChildProfile: json['isChildProfile'] as bool? ?? false,
    language: json['language'] as String? ?? 'pt',
  );

  Map<String, dynamic> toJson() => {
    'id': id,
    'email': email,
    'displayName': displayName,
    'isChildProfile': isChildProfile,
    'language': language,
  };
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

  Map<String, dynamic> toJson() => {
    'id': id,
    'name': name,
    'role': role.apiValue,
    'access': ModuleAccess.toJson(access),
  };
}

/// Uma alteração no histórico da casa. [canUndo]: quem está a usar a app pode desfazê-la agora.
class ActivityItem {
  const ActivityItem({
    required this.id,
    required this.userId,
    required this.summary,
    required this.createdAt,
    this.module,
    this.canUndo = false,
    this.undoneAt,
    this.undoneByUserId,
    this.isUndo = false,
  });

  final int id;
  final String userId;

  /// A área; null nas alterações à casa e aos membros.
  final HouseholdModule? module;

  /// O que aconteceu, para pôr depois do nome: "pôs «Leite» na lista «Supermercado»".
  final String summary;
  final DateTime createdAt;
  final bool canUndo;
  final DateTime? undoneAt;
  final String? undoneByUserId;

  /// Esta entrada é o desfazer de outra.
  final bool isUndo;

  factory ActivityItem.fromJson(Map<String, dynamic> json) => ActivityItem(
    id: json['id'] as int,
    userId: json['userId'] as String,
    module: json['module'] == null ? null : HouseholdModule.fromApi(json['module'] as String),
    summary: json['summary'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String).toLocal(),
    canUndo: json['canUndo'] as bool? ?? false,
    undoneAt: json['undoneAt'] == null ? null : DateTime.parse(json['undoneAt'] as String).toLocal(),
    undoneByUserId: json['undoneByUserId'] as String?,
    isUndo: json['isUndo'] as bool? ?? false,
  );
}

/// Um modelo para criar a casa já com listas, tarefas e recompensas ("Família com crianças", "Casa partilhada"…).
class HouseholdTemplate {
  const HouseholdTemplate({
    required this.key,
    required this.name,
    required this.description,
    this.lists = const [],
    this.chores = const [],
    this.rewards = const [],
  });

  /// "FamilyWithKids", "Couple", "SharedHouse", "CaringForParents" ou "Blank".
  final String key;
  final String name;
  final String description;
  final List<String> lists;
  final List<String> chores;
  final List<String> rewards;

  bool get isBlank => key == 'Blank';

  /// "2 listas · 5 tarefas · 3 recompensas"
  String get contents => [
    if (lists.isNotEmpty) '${lists.length} ${lists.length == 1 ? 'lista'.tr : 'listas'.tr}',
    if (chores.isNotEmpty) '${chores.length} ${chores.length == 1 ? 'tarefa'.tr : 'tarefas'.tr}',
    if (rewards.isNotEmpty) '${rewards.length} ${rewards.length == 1 ? 'recompensa'.tr : 'recompensas'.tr}',
  ].join(' · ');

  factory HouseholdTemplate.fromJson(Map<String, dynamic> json) => HouseholdTemplate(
    key: json['key'] as String,
    name: json['name'] as String,
    description: json['description'] as String,
    lists: (json['lists'] as List).cast<String>(),
    chores: (json['chores'] as List).cast<String>(),
    rewards: (json['rewards'] as List).cast<String>(),
  );
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

  /// Para guardar no aparelho a última resposta (sem segredos) e abrir a app sem rede.
  Map<String, dynamic> toJson() => {
    'user': user.toJson(),
    'households': [for (final h in households) h.toJson()],
    'isSupport': isSupport,
  };
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
    this.avatar,
  });

  final String userId;
  final String displayName;
  final HouseholdRole role;
  final String color;

  /// O emoji do avatar (F4-03); null mostra a inicial.
  final String? avatar;

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
    avatar: json['avatar'] as String?,
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
  const ChildProfileOption({required this.userId, required this.displayName, required this.color, this.avatar});

  final String userId;
  final String displayName;
  final String color;
  final String? avatar;

  factory ChildProfileOption.fromJson(Map<String, dynamic> json) => ChildProfileOption(
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
    color: json['color'] as String,
    avatar: json['avatar'] as String?,
  );
}

/// O que o ecrã Hoje e o painel de cozinha precisam, num só pedido (`GET /households/{id}/today`). As áreas que a
/// pessoa não vê vêm a null.
/// Um artigo que se costuma comprar e que já passou do costume ("Costumas comprar", F4-22b).
class PurchaseSuggestion {
  const PurchaseSuggestion({required this.name, required this.category, required this.everyDays});

  final String name;
  final String? category;

  /// De quantos em quantos dias se costuma comprar.
  final int everyDays;

  factory PurchaseSuggestion.fromJson(Map<String, dynamic> json) => PurchaseSuggestion(
    name: json['name'] as String,
    category: json['category'] as String?,
    everyDays: json['everyDays'] as int,
  );
}

/// As sugestões e a ordem das categorias aprendida numa lista.
class ShoppingInsights {
  const ShoppingInsights({required this.suggestions, required this.categoryOrder});

  final List<PurchaseSuggestion> suggestions;
  final List<String> categoryOrder;

  factory ShoppingInsights.fromJson(Map<String, dynamic> json) => ShoppingInsights(
    suggestions: [for (final s in json['suggestions'] as List) PurchaseSuggestion.fromJson(s as Map<String, dynamic>)],
    categoryOrder: (json['categoryOrder'] as List).cast<String>(),
  );
}

/// Um artigo que volta à lista sozinho a cada [intervalDays] dias (F4-22a).
class RecurringItem {
  const RecurringItem({
    required this.id,
    required this.name,
    required this.quantity,
    required this.intervalDays,
    required this.nextDate,
  });

  final String id;
  final String name;
  final String? quantity;
  final int intervalDays;
  final DateTime nextDate;

  factory RecurringItem.fromJson(Map<String, dynamic> json) => RecurringItem(
    id: json['id'] as String,
    name: json['name'] as String,
    quantity: json['quantity'] as String?,
    intervalDays: json['intervalDays'] as int,
    nextDate: DateTime.parse(json['nextDate'] as String),
  );
}

/// Um ingrediente de uma receita: o nome (como vai para a lista), a quantidade e a secção do supermercado.
class RecipeIngredient {
  const RecipeIngredient({required this.name, this.quantity, this.category});

  final String name;
  final String? quantity;
  final String? category;

  factory RecipeIngredient.fromJson(Map<String, dynamic> json) => RecipeIngredient(
    name: json['name'] as String,
    quantity: json['quantity'] as String?,
    category: json['category'] as String?,
  );

  Map<String, dynamic> toJson() => {'name': name, 'quantity': ?quantity, 'category': ?category};

  @override
  String toString() => quantity == null ? name : '$quantity $name';
}

/// Uma receita da casa (F4-09).
class Recipe {
  const Recipe({
    required this.id,
    required this.title,
    required this.servings,
    required this.ingredients,
    required this.steps,
    required this.tags,
    required this.notes,
  });

  final String id;
  final String title;
  final int servings;
  final List<RecipeIngredient> ingredients;
  final List<String> steps;
  final List<String> tags;
  final String? notes;

  factory Recipe.fromJson(Map<String, dynamic> json) => Recipe(
    id: json['id'] as String,
    title: json['title'] as String,
    servings: json['servings'] as int? ?? 4,
    ingredients: [
      for (final i in json['ingredients'] as List? ?? const []) RecipeIngredient.fromJson(i as Map<String, dynamic>),
    ],
    steps: (json['steps'] as List?)?.cast<String>() ?? const [],
    tags: (json['tags'] as List?)?.cast<String>() ?? const [],
    notes: json['notes'] as String?,
  );
}

/// Dados para criar ou mudar uma receita.
class RecipeDraft {
  const RecipeDraft({
    required this.title,
    this.servings = 4,
    this.ingredients = const [],
    this.steps = const [],
    this.tags = const [],
    this.notes,
  });

  final String title;
  final int servings;
  final List<RecipeIngredient> ingredients;
  final List<String> steps;
  final List<String> tags;
  final String? notes;

  Map<String, dynamic> toJson() => {
    'title': title,
    'servings': servings,
    'ingredients': [for (final i in ingredients) i.toJson()],
    'steps': steps,
    'tags': tags,
    if (notes != null && notes!.isNotEmpty) 'notes': notes,
  };
}

/// Almoço ou jantar.
enum MealSlot {
  lunch('Lunch', 'Almoço'),
  dinner('Dinner', 'Jantar');

  const MealSlot(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  static MealSlot fromApi(Object? value) => values.firstWhere((s) => s.apiValue == value, orElse: () => lunch);
}

/// O que se come num dia, a uma refeição: uma receita da casa ou um texto ("restos", "jantar fora").
class MealPlanEntry {
  const MealPlanEntry({required this.date, required this.slot, required this.recipeId, required this.text});

  final DateTime date;
  final MealSlot slot;
  final String? recipeId;
  final String? text;

  bool get isEmpty => recipeId == null && (text == null || text!.isEmpty);

  factory MealPlanEntry.fromJson(Map<String, dynamic> json) => MealPlanEntry(
    date: DateTime.parse(json['date'] as String),
    slot: MealSlot.fromApi(json['slot']),
    recipeId: json['recipeId'] as String?,
    text: json['text'] as String?,
  );
}

/// Uma despesa partilhada (F4-10): quem pagou, quanto e a parte de cada um.
class Expense {
  const Expense({
    required this.id,
    required this.description,
    required this.amountCents,
    required this.paidByUserId,
    required this.date,
    required this.shares,
    required this.createdByUserId,
  });

  final String id;
  final String description;
  final int amountCents;
  final String paidByUserId;
  final DateTime date;
  final List<({String userId, int amountCents})> shares;
  final String createdByUserId;

  factory Expense.fromJson(Map<String, dynamic> json) => Expense(
    id: json['id'] as String,
    description: json['description'] as String,
    amountCents: json['amountCents'] as int,
    paidByUserId: json['paidByUserId'] as String,
    date: DateTime.parse(json['date'] as String),
    shares: [
      for (final s in json['shares'] as List)
        (userId: (s as Map)['userId'] as String, amountCents: s['amountCents'] as int),
    ],
    createdByUserId: json['createdByUserId'] as String,
  );
}

/// Dados para registar uma despesa, dividida em partes iguais por [splitWith].
class ExpenseDraft {
  const ExpenseDraft({
    required this.description,
    required this.amountCents,
    required this.paidByUserId,
    required this.date,
    required this.splitWith,
  });

  final String description;
  final int amountCents;
  final String paidByUserId;
  final DateTime date;
  final List<String> splitWith;

  Map<String, dynamic> toJson() => {
    'description': description,
    'amountCents': amountCents,
    'paidByUserId': paidByUserId,
    'date': apiDate(date),
    'splitWith': splitWith,
  };
}

/// Quem paga a quem para ficarem acertados.
class ExpenseTransfer {
  const ExpenseTransfer({required this.fromUserId, required this.toUserId, required this.amountCents});

  final String fromUserId;
  final String toUserId;
  final int amountCents;

  factory ExpenseTransfer.fromJson(Map<String, dynamic> json) => ExpenseTransfer(
    fromUserId: json['fromUserId'] as String,
    toUserId: json['toUserId'] as String,
    amountCents: json['amountCents'] as int,
  );
}

/// As despesas recentes, o saldo de cada um (positivo: recebe; negativo: deve) e as transferências para acertar.
class ExpensesOverview {
  const ExpensesOverview({required this.expenses, required this.balances, required this.transfers});

  final List<Expense> expenses;
  final List<({String userId, int netCents})> balances;
  final List<ExpenseTransfer> transfers;

  factory ExpensesOverview.fromJson(Map<String, dynamic> json) => ExpensesOverview(
    expenses: [for (final e in json['expenses'] as List) Expense.fromJson(e as Map<String, dynamic>)],
    balances: [
      for (final b in json['balances'] as List)
        (userId: (b as Map)['userId'] as String, netCents: b['netCents'] as int),
    ],
    transfers: [for (final t in json['transfers'] as List) ExpenseTransfer.fromJson(t as Map<String, dynamic>)],
  );
}

DateTime? _optionalDay(Object? value) => value == null ? null : DateTime.parse(value as String);

/// Um recado do quadro da casa (F4-12).
class HouseNote {
  const HouseNote({required this.id, required this.text, required this.until, required this.createdByUserId});

  final String id;
  final String text;
  final DateTime? until;
  final String createdByUserId;

  factory HouseNote.fromJson(Map<String, dynamic> json) => HouseNote(
    id: json['id'] as String,
    text: json['text'] as String,
    until: _optionalDay(json['until']),
    createdByUserId: json['createdByUserId'] as String,
  );
}

/// Um ficheiro de um equipamento ou documento.
class HomeFile {
  const HomeFile({required this.id, required this.fileName, required this.contentType, required this.size});

  final String id;
  final String fileName;
  final String contentType;
  final int size;

  bool get isImage => contentType.startsWith('image/');

  factory HomeFile.fromJson(Map<String, dynamic> json) => HomeFile(
    id: json['id'] as String,
    fileName: json['fileName'] as String,
    contentType: json['contentType'] as String,
    size: json['size'] as int,
  );
}

/// Uma manutenção periódica de um equipamento.
class MaintenanceTask {
  const MaintenanceTask({required this.id, required this.title, required this.intervalDays, required this.nextDue});

  final String id;
  final String title;
  final int intervalDays;
  final DateTime nextDue;

  factory MaintenanceTask.fromJson(Map<String, dynamic> json) => MaintenanceTask(
    id: json['id'] as String,
    title: json['title'] as String,
    intervalDays: json['intervalDays'] as int,
    nextDue: DateTime.parse(json['nextDue'] as String),
  );
}

/// Um equipamento da casa (F4-07); num carro, o próximo IUC e a próxima inspeção (F4-08).
class Appliance {
  const Appliance({
    required this.id,
    required this.name,
    required this.isCar,
    required this.room,
    required this.brand,
    required this.warrantyUntil,
    required this.plate,
    required this.firstRegistration,
    required this.nextIuc,
    required this.nextInspection,
    required this.maintenance,
    required this.files,
  });

  final String id;
  final String name;
  final bool isCar;
  final String? room;
  final String? brand;
  final DateTime? warrantyUntil;
  final String? plate;
  final DateTime? firstRegistration;
  final DateTime? nextIuc;
  final DateTime? nextInspection;
  final List<MaintenanceTask> maintenance;
  final List<HomeFile> files;

  factory Appliance.fromJson(Map<String, dynamic> json) => Appliance(
    id: json['id'] as String,
    name: json['name'] as String,
    isCar: json['kind'] == 'Car',
    room: json['room'] as String?,
    brand: json['brand'] as String?,
    warrantyUntil: _optionalDay(json['warrantyUntil']),
    plate: json['plate'] as String?,
    firstRegistration: _optionalDay(json['firstRegistration']),
    nextIuc: _optionalDay(json['nextIuc']),
    nextInspection: _optionalDay(json['nextInspection']),
    maintenance: [for (final m in json['maintenance'] as List) MaintenanceTask.fromJson(m as Map<String, dynamic>)],
    files: [for (final f in json['files'] as List) HomeFile.fromJson(f as Map<String, dynamic>)],
  );
}

/// Os tipos de documento do cofre.
enum DocumentKind {
  citizenCard('CitizenCard', 'Cartão de cidadão'),
  passport('Passport', 'Passaporte'),
  drivingLicence('DrivingLicence', 'Carta de condução'),
  vaccination('Vaccination', 'Boletim de vacinas'),
  insurance('Insurance', 'Seguro'),
  other('Other', 'Outro');

  const DocumentKind(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  static DocumentKind fromApi(Object? value) => values.firstWhere((k) => k.apiValue == value, orElse: () => other);
}

/// Um documento do cofre (F4-13). [number] só chega ao dono e a quem gere a área.
class HomeDocument {
  const HomeDocument({
    required this.id,
    required this.kind,
    required this.name,
    required this.ownerUserId,
    required this.number,
    required this.expiresOn,
    required this.files,
  });

  final String id;
  final DocumentKind kind;
  final String name;
  final String? ownerUserId;
  final String? number;
  final DateTime? expiresOn;
  final List<HomeFile> files;

  factory HomeDocument.fromJson(Map<String, dynamic> json) => HomeDocument(
    id: json['id'] as String,
    kind: DocumentKind.fromApi(json['kind']),
    name: json['name'] as String,
    ownerUserId: json['ownerUserId'] as String?,
    number: json['number'] as String?,
    expiresOn: _optionalDay(json['expiresOn']),
    files: [for (final f in json['files'] as List) HomeFile.fromJson(f as Map<String, dynamic>)],
  );
}

/// Um contacto útil.
class HouseContact {
  const HouseContact({required this.id, required this.name, this.role, this.phone, this.birthday});

  final String id;
  final String name;
  final String? role;
  final String? phone;
  final DateTime? birthday;

  factory HouseContact.fromJson(Map<String, dynamic> json) => HouseContact(
    id: json['id'] as String,
    name: json['name'] as String,
    role: json['role'] as String?,
    phone: json['phone'] as String?,
    birthday: _optionalDay(json['birthday']),
  );
}

/// Tudo o que a área Casa mostra.
class HomeOverview {
  const HomeOverview({
    required this.appliances,
    required this.documents,
    required this.notes,
    required this.contacts,
    required this.birthdays,
    required this.irsReminders,
  });

  final List<Appliance> appliances;
  final List<HomeDocument> documents;
  final List<HouseNote> notes;
  final List<HouseContact> contacts;
  final List<({String name, DateTime date})> birthdays;
  final bool irsReminders;

  factory HomeOverview.fromJson(Map<String, dynamic> json) => HomeOverview(
    appliances: [for (final a in json['appliances'] as List) Appliance.fromJson(a as Map<String, dynamic>)],
    documents: [for (final d in json['documents'] as List) HomeDocument.fromJson(d as Map<String, dynamic>)],
    notes: [for (final n in json['notes'] as List) HouseNote.fromJson(n as Map<String, dynamic>)],
    contacts: [for (final c in json['contacts'] as List) HouseContact.fromJson(c as Map<String, dynamic>)],
    birthdays: [
      for (final b in json['birthdays'] as List)
        (name: (b as Map)['name'] as String, date: DateTime.parse(b['date'] as String)),
    ],
    irsReminders: json['irsReminders'] as bool? ?? true,
  );
}

class TodayBundle {
  const TodayBundle({
    required this.details,
    required this.absences,
    required this.chores,
    required this.calendar,
    required this.lists,
    required this.rewards,
    this.notes,
  });

  final HouseholdDetails details;
  final List<MemberAbsence> absences;
  final List<Chore>? chores;
  final CalendarRange? calendar;
  final List<ShoppingList>? lists;
  final RewardsOverview? rewards;

  /// Os recados da casa (null: sem acesso à área Casa).
  final List<HouseNote>? notes;
}

class HouseholdDetails {
  const HouseholdDetails({
    required this.id,
    required this.name,
    required this.timeZoneId,
    required this.myRole,
    required this.members,
    this.myAccess = const {},
    this.utcOffsetMinutes,
  });

  final String id;
  final String name;
  final String timeZoneId;
  final HouseholdRole myRole;
  final List<Member> members;

  /// O desvio atual do fuso da casa em relação a UTC, em minutos (null numa API antiga).
  final int? utcOffsetMinutes;

  /// Que dia é na casa a [now] (o telemóvel pode estar noutro fuso: quem viaja, a guarda partilhada). Sem o desvio,
  /// usa o dia do telemóvel.
  DateTime todayAt(DateTime now) {
    final offset = utcOffsetMinutes;
    if (offset == null) return DateTime(now.year, now.month, now.day);
    final there = now.toUtc().add(Duration(minutes: offset));
    return DateTime(there.year, there.month, there.day);
  }

  /// O que o meu papel me deixa fazer em cada área (o habitual, se não vier).
  final Map<HouseholdModule, ModuleAccess> myAccess;

  ModuleAccess accessTo(HouseholdModule module) => myAccess[module] ?? ModuleAccess.defaultFor(myRole, module);

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
    myRole: HouseholdRole.fromApi(json['myRole']),
    members: (json['members'] as List).map((m) => Member.fromJson(m as Map<String, dynamic>)).toList(),
    myAccess: ModuleAccess.parse(json['myAccess'], HouseholdRole.fromApi(json['myRole'])),
    utcOffsetMinutes: json['utcOffsetMinutes'] as int?,
  );
}

class Invite {
  const Invite({required this.code, required this.role, required this.expiresAt, this.maxUses, this.usedCount = 0});

  final String code;
  final HouseholdRole role;
  final DateTime expiresAt;

  /// Quantas pessoas podem entrar com ele (null = sem limite até expirar); [usedCount] já entraram.
  final int? maxUses;
  final int usedCount;

  factory Invite.fromJson(Map<String, dynamic> json) => Invite(
    code: json['code'] as String,
    role: HouseholdRole.fromApi(json['role']),
    expiresAt: DateTime.parse(json['expiresAt'] as String).toLocal(),
    maxUses: json['maxUses'] as int?,
    usedCount: json['usedCount'] as int? ?? 0,
  );
}

/// O que acontece a uma casa se a conta for apagada.
enum AccountDeletionOutcome {
  /// Mais ninguém lá tem conta: a casa é apagada.
  deleted('Deleted'),

  /// A administração passa para outra pessoa ([AccountDeletionHousehold.successorName]).
  adminPassesTo('AdminPassesTo'),

  /// És o único administrador e não há outro adulto: passa a administração antes.
  blocked('Blocked'),

  /// Sais, e a casa continua como está.
  leaves('Leaves');

  const AccountDeletionOutcome(this.apiValue);

  final String apiValue;

  static AccountDeletionOutcome fromApi(Object? value) =>
      values.firstWhere((o) => o.apiValue == value, orElse: () => leaves);
}

class AccountDeletionHousehold {
  const AccountDeletionHousehold({
    required this.householdId,
    required this.householdName,
    required this.outcome,
    this.successorName,
  });

  final String householdId;
  final String householdName;
  final AccountDeletionOutcome outcome;
  final String? successorName;

  factory AccountDeletionHousehold.fromJson(Map<String, dynamic> json) => AccountDeletionHousehold(
    householdId: json['householdId'] as String,
    householdName: json['householdName'] as String,
    outcome: AccountDeletionOutcome.fromApi(json['outcome']),
    successorName: json['successorName'] as String?,
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
    this.position = 0,
  });

  final String id;
  final String householdId;
  final String name;
  final int itemCount;
  final int checkedCount;

  /// A ordem das listas na casa (arrastar para reordenar).
  final int position;

  /// Pela ordem escolhida e, entre as que têm a mesma, pelo nome.
  static int compare(ShoppingList a, ShoppingList b) {
    final byPosition = a.position.compareTo(b.position);
    return byPosition != 0 ? byPosition : a.name.toLowerCase().compareTo(b.name.toLowerCase());
  }

  int get pendingCount => itemCount - checkedCount;

  Map<String, dynamic> toJson() => {
    'id': id,
    'householdId': householdId,
    'name': name,
    'itemCount': itemCount,
    'checkedCount': checkedCount,
    'position': position,
  };

  factory ShoppingList.fromJson(Map<String, dynamic> json) => ShoppingList(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    name: json['name'] as String,
    itemCount: json['itemCount'] as int,
    checkedCount: json['checkedCount'] as int,
    position: json['position'] as int? ?? 0,
  );

  ShoppingList withPosition(int position) => ShoppingList(
    id: id,
    householdId: householdId,
    name: name,
    itemCount: itemCount,
    checkedCount: checkedCount,
    position: position,
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
    this.position,
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

  /// A ordem escolhida à mão dentro da categoria (null: pela ordem em que se pôs, depois dos arrumados).
  final int? position;

  ShoppingItem copyWith({bool? isChecked, int? position}) => ShoppingItem(
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
    position: position ?? this.position,
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
    'position': ?position,
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
    position: json['position'] as int?,
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

  const ChoreRecurrence(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  static ChoreRecurrence fromApi(Object? value) => values.firstWhere((r) => r.apiValue == value, orElse: () => none);
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
    this.requiresPhoto = false,
    this.availableFrom,
    this.remindAt,
    this.fairRotation = false,
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

  /// Para concluir é preciso uma foto do trabalho feito.
  final bool requiresPhoto;

  /// Numa tarefa que se repete, desde quando se pode fazer a ocorrência atual (cada uma faz-se uma vez; não se
  /// adiantam as seguintes). Null nas que não se repetem.
  final DateTime? availableFrom;

  /// A que horas (no fuso da casa) se lembra a quem a tem, no dia em que é para fazer.
  final ({int hour, int minute})? remindAt;

  /// Na rotação, a próxima vez vai para quem fez menos tarefas (F4-23).
  final bool fairRotation;

  /// Se já se pode concluir hoje.
  bool isAvailableOn(DateTime today) {
    final from = availableFrom;
    return from == null || !DateTime(today.year, today.month, today.day).isBefore(from);
  }

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
    requiresPhoto: requiresPhoto,
    availableFrom: availableFrom,
    remindAt: remindAt,
    fairRotation: fairRotation,
  );

  factory Chore.fromJson(Map<String, dynamic> json) => Chore(
    id: json['id'] as String,
    householdId: json['householdId'] as String,
    title: json['title'] as String,
    notes: json['notes'] as String?,
    points: json['points'] as int,
    dueDate: json['dueDate'] == null ? null : DateTime.parse(json['dueDate'] as String),
    recurrence: ChoreRecurrence.fromApi(json['recurrence']),
    assigneeUserId: json['assigneeUserId'] as String?,
    rotationUserIds: (json['rotationUserIds'] as List).cast<String>(),
    isDone: json['isDone'] as bool,
    updatedAt: DateTime.parse(json['updatedAt'] as String),
    timeOfDay: ChoreTimeOfDay.fromApi(json['timeOfDay']),
    steps: (json['steps'] as List?)?.cast<String>() ?? const [],
    doneSteps: (json['doneSteps'] as List?)?.cast<int>() ?? const [],
    requiresPhoto: json['requiresPhoto'] as bool? ?? false,
    availableFrom: json['availableFrom'] == null ? null : DateTime.parse(json['availableFrom'] as String),
    remindAt: json['remindAt'] == null ? null : _time(json['remindAt'] as String),
    fairRotation: json['fairRotation'] as bool? ?? false,
  );
}

/// Uma tarefa feita. [rejected]: quem gere as tarefas não a aceitou (os pontos não contam).
class ChoreHistoryItem {
  const ChoreHistoryItem({
    required this.id,
    required this.choreId,
    required this.choreTitle,
    required this.userId,
    required this.points,
    required this.completedAt,
    this.photoId,
    this.rejected = false,
  });

  final String id;
  final String choreId;
  final String choreTitle;
  final String userId;
  final int points;
  final DateTime completedAt;
  final String? photoId;
  final bool rejected;

  factory ChoreHistoryItem.fromJson(Map<String, dynamic> json) => ChoreHistoryItem(
    id: json['id'] as String,
    choreId: json['choreId'] as String,
    choreTitle: json['choreTitle'] as String,
    userId: json['userId'] as String,
    points: json['points'] as int,
    completedAt: DateTime.parse(json['completedAt'] as String).toLocal(),
    photoId: json['photoId'] as String?,
    rejected: json['rejected'] as bool? ?? false,
  );
}

/// Em que altura do dia se faz uma tarefa.
enum ChoreTimeOfDay {
  morning('Morning', 'Manhã'),
  any('Any', 'Qualquer altura'),
  evening('Evening', 'Noite');

  const ChoreTimeOfDay(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  static ChoreTimeOfDay fromApi(Object? value) => values.firstWhere((t) => t.apiValue == value, orElse: () => any);
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
    this.requiresPhoto = false,
    this.remindAt,
    this.fairRotation = false,
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
  final bool requiresPhoto;

  /// A que horas se lembra a quem a tem, no dia em que é para fazer.
  final ({int hour, int minute})? remindAt;

  /// Na rotação, a próxima vez vai para quem fez menos tarefas.
  final bool fairRotation;

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
    requiresPhoto: chore.requiresPhoto,
    remindAt: chore.remindAt,
    fairRotation: chore.fairRotation,
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
    'requiresPhoto': requiresPhoto,
    if (remindAt != null) 'remindAt': _timeJson(remindAt!),
    if (fairRotation) 'fairRotation': true,
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
    this.reminderMinutes = const [],
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

  /// Lembretes, em minutos antes do início (nos de dia inteiro, antes das 9h do primeiro dia).
  final List<int> reminderMinutes;

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
    reminderMinutes: (json['reminderMinutes'] as List?)?.cast<int>() ?? const [],
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
  CalendarRange({required this.events, required this.occurrences, required this.holidays});

  final Map<String, CalendarEvent> events;
  final List<CalendarOccurrence> occurrences;
  final List<Holiday> holidays;

  /// As ocorrências de cada dia (chave [dayKey]), pela ordem do servidor, construídas uma vez (P5): antes, cada uma das
  /// 42 células do mês percorria todas as ocorrências (milhares numa casa cheia) a cada desenho.
  late final Map<int, List<CalendarOccurrence>> _byDay = _indexByDay();

  late final Map<String, Set<String>> _conflicts = _indexConflicts();

  late final Map<int, Holiday> _holidays = {for (final h in holidays.reversed) dayKey(h.date): h};

  /// Um dia como número (aaaammdd), para comparar sem criar datas.
  static int dayKey(DateTime day) => day.year * 10000 + day.month * 100 + day.day;

  List<CalendarOccurrence> on(DateTime day) => _byDay[dayKey(day)] ?? const [];

  /// Membros indisponíveis para alguma ocorrência de [eventId].
  Set<String> conflictsFor(String eventId) => _conflicts[eventId] ?? const {};

  Holiday? holidayOn(DateTime day) => _holidays[dayKey(day)];

  /// Cada ocorrência entra nos dias que ocupa. Os dias candidatos vão de um antes do início a um depois do fim, e
  /// cada um confirma-se com [CalendarOccurrence.occursOn], para dar exatamente o mesmo que filtrar dia a dia.
  Map<int, List<CalendarOccurrence>> _indexByDay() {
    final index = <int, List<CalendarOccurrence>>{};
    for (final o in occurrences) {
      if (!events.containsKey(o.eventId)) continue;
      final a = o.isAllDay ? o.startDate! : o.startsAt!;
      final b = o.isAllDay ? o.endDate! : o.endsAt!;
      final (first, last) = a.isAfter(b) ? (b, a) : (a, b);
      for (
        var day = DateTime(first.year, first.month, first.day - 1);
        !day.isAfter(DateTime(last.year, last.month, last.day + 1));
        day = DateTime(day.year, day.month, day.day + 1)
      ) {
        if (o.occursOn(day)) (index[dayKey(day)] ??= []).add(o);
      }
    }
    return index;
  }

  Map<String, Set<String>> _indexConflicts() {
    final index = <String, Set<String>>{};
    for (final o in occurrences) {
      (index[o.eventId] ??= {}).addAll(o.conflictUserIds);
    }
    return index;
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
    this.reminderMinutes = const [],
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
  final List<int> reminderMinutes;

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
    reminderMinutes: reminderMinutes,
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
    reminderMinutes: event.reminderMinutes,
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
    if (reminderMinutes.isNotEmpty) 'reminderMinutes': reminderMinutes,
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
/// O Google e a Microsoft ligam-se por uma página de autorização; o iCloud com o Apple ID e uma palavra-passe da app.
enum CalendarProvider {
  google('Google', 'Google Calendar', 'conta Google'),
  microsoft('Microsoft', 'Outlook', 'conta Microsoft'),
  apple('Apple', 'iCloud', 'conta Apple');

  const CalendarProvider(this.apiValue, this._label, this._account);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  final String _account;

  /// Como se chama a conta ("Ligar a conta Google").
  String get account => _account.tr;

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

  const RecurrenceFrequency(this.apiValue, this._label, this._unit);

  final String apiValue;
  final String _label;

  String get label => _label.tr;
  final String _unit;

  String get unit => _unit.tr;
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

  const RedemptionStatus(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

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
/// Euros como se escrevem em Portugal, "6,20 €" (ou "6,20", sem o símbolo); em inglês, "€6.20" (ou "6.20").
String formatEuros(int cents, {bool symbol = true}) {
  final english = AppLanguage.isEnglish;
  final text = '${cents ~/ 100}${english ? '.' : ','}${(cents % 100).toString().padLeft(2, '0')}';
  return !symbol
      ? text
      : english
      ? '€$text'
      : '$text €';
}

/// "5", "5,5", "5,50", "5.50" ou "€5.50" → cêntimos; null se não for um valor válido.
int? parseEuros(String text) {
  final match = RegExp(r'^\s*€?\s*(\d{1,4})(?:[.,](\d{1,2}))?\s*€?\s*$').firstMatch(text);
  if (match == null) return null;
  final cents = (match.group(2) ?? '0').padRight(2, '0');
  return int.parse(match.group(1)!) * 100 + int.parse(cents);
}

enum AllowancePeriod {
  weekly('Weekly', 'por semana'),
  monthly('Monthly', 'por mês');

  const AllowancePeriod(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

  static AllowancePeriod fromApi(String value) => values.firstWhere((p) => p.apiValue == value, orElse: () => weekly);
}

/// Um período da mesada: o atual (a decorrer), um por pagar, ou um pago.
class AllowancePeriodEntry {
  const AllowancePeriodEntry({
    required this.start,
    required this.end,
    required this.choresDone,
    required this.amountCents,
    required this.isCurrent,
    this.paymentId,
    this.paidAt,
    this.owedCents = 0,
  });

  final DateTime start;
  final DateTime end;
  final int choresDone;

  /// O valor do período (nos pagos, o que se pagou).
  final int amountCents;
  final bool isCurrent;
  final String? paymentId;
  final DateTime? paidAt;

  /// O que ainda falta pagar: tudo, se não foi pago; num pago antes de acabar, o extra das tarefas feitas depois.
  final int owedCents;

  bool get isPaid => paidAt != null;

  factory AllowancePeriodEntry.fromJson(Map<String, dynamic> json) => AllowancePeriodEntry(
    start: DateTime.parse(json['start'] as String),
    end: DateTime.parse(json['end'] as String),
    choresDone: json['choresDone'] as int,
    amountCents: json['amountCents'] as int,
    isCurrent: json['isCurrent'] as bool,
    paymentId: json['paymentId'] as String?,
    paidAt: json['paidAt'] == null ? null : DateTime.parse(json['paidAt'] as String).toLocal(),
    owedCents: json['owedCents'] as int? ?? 0,
  );
}

/// A mesada de um membro: valor por semana ou por mês, mais um extra por tarefa feita.
class Allowance {
  const Allowance({
    required this.userId,
    required this.amountCents,
    required this.bonusPerChoreCents,
    required this.period,
    required this.owedCents,
    required this.periods,
  });

  final String userId;
  final int amountCents;
  final int bonusPerChoreCents;
  final AllowancePeriod period;

  /// Quanto falta pagar dos períodos que já acabaram.
  final int owedCents;

  /// Do mais recente (o atual) para o mais antigo.
  final List<AllowancePeriodEntry> periods;

  /// "5,00 € por semana + 0,50 € por tarefa feita"
  String describe() => [
    '${formatEuros(amountCents)} ${period.label}',
    if (bonusPerChoreCents > 0) '+ ${formatEuros(bonusPerChoreCents)} por tarefa feita'.tr,
  ].join(' ');

  factory Allowance.fromJson(Map<String, dynamic> json) => Allowance(
    userId: json['userId'] as String,
    amountCents: json['amountCents'] as int,
    bonusPerChoreCents: json['bonusPerChoreCents'] as int,
    period: AllowancePeriod.fromApi(json['period'] as String),
    owedCents: json['owedCents'] as int,
    periods: [for (final p in json['periods'] as List) AllowancePeriodEntry.fromJson(p as Map<String, dynamic>)],
  );
}

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
  const RewardsOverview({required this.rewards, required this.redemptions, required this.points, this.autoApproveUpTo});

  final List<Reward> rewards;
  final List<RewardRedemption> redemptions;
  final List<MemberPoints> points;

  /// Os pedidos até este custo aprovam-se sozinhos; null = todos esperam por um adulto.
  final int? autoApproveUpTo;

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
      autoApproveUpTo: json['autoApproveUpTo'] as int?,
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

  const NotificationDelivery(this.apiValue, this._label);

  final String apiValue;
  final String _label;

  String get label => _label.tr;

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
    this.morningSummary = true,
    this.morningAt = (hour: 7, minute: 30),
    this.weeklySummary = true,
  });

  final NotificationDelivery delivery;
  final ({int hour, int minute}) digestAt;
  final ({int hour, int minute})? quietFrom;
  final ({int hour, int minute})? quietUntil;
  final String timeZoneId;

  /// O resumo da manhã (o que há hoje), à hora [morningAt] no fuso da casa.
  final bool morningSummary;
  final ({int hour, int minute}) morningAt;

  /// O resumo da semana (domingo às 19h), para quem gere as tarefas.
  final bool weeklySummary;

  factory NotificationPreferences.fromJson(Map<String, dynamic> json) => NotificationPreferences(
    delivery: NotificationDelivery.fromApi(json['delivery'] as String),
    digestAt: _time(json['digestAt'] as String),
    quietFrom: json['quietFrom'] == null ? null : _time(json['quietFrom'] as String),
    quietUntil: json['quietUntil'] == null ? null : _time(json['quietUntil'] as String),
    timeZoneId: json['timeZoneId'] as String,
    morningSummary: json['morningSummary'] as bool? ?? true,
    morningAt: json['morningAt'] == null ? (hour: 7, minute: 30) : _time(json['morningAt'] as String),
    weeklySummary: json['weeklySummary'] as bool? ?? true,
  );

  Map<String, dynamic> toJson() => {
    'delivery': delivery.apiValue,
    'digestAt': _timeJson(digestAt),
    'quietFrom': quietFrom == null ? null : _timeJson(quietFrom!),
    'quietUntil': quietUntil == null ? null : _timeJson(quietUntil!),
    'timeZoneId': timeZoneId,
    'morningSummary': morningSummary,
    'morningAt': _timeJson(morningAt),
    'weeklySummary': weeklySummary,
  };
}

// Assistente (F4-24).

/// O assistente nesta casa: se existe no servidor (e para esta pessoa), se a casa o tem ligado e o uso de hoje.
class AssistantStatus {
  const AssistantStatus({
    required this.available,
    required this.enabled,
    required this.usedToday,
    required this.dailyLimit,
  });

  final bool available;
  final bool enabled;
  final int usedToday;
  final int dailyLimit;

  factory AssistantStatus.fromJson(Map<String, dynamic> json) => AssistantStatus(
    available: json['available'] as bool? ?? false,
    enabled: json['enabled'] as bool? ?? false,
    usedToday: json['usedToday'] as int? ?? 0,
    dailyLimit: json['dailyLimit'] as int? ?? 0,
  );
}

enum AssistantActionKind { addItems, checkItems, createEvent, createChore, createExpense }

/// Uma ação proposta pelo assistente, já validada no servidor. Só acontece se a pessoa a escolher.
class AssistantAction {
  const AssistantAction({
    required this.kind,
    this.listId,
    this.listName,
    this.items = const [],
    this.itemIds = const [],
    this.title,
    this.date,
    this.endDate,
    this.startsAt,
    this.endsAt,
    this.amountCents,
    this.location,
  });

  final AssistantActionKind kind;
  final String? listId;
  final String? listName;
  final List<String> items;
  final List<String> itemIds;
  final String? title;
  final DateTime? date;
  final DateTime? endDate;
  final DateTime? startsAt;
  final DateTime? endsAt;
  final int? amountCents;
  final String? location;

  factory AssistantAction.fromJson(Map<String, dynamic> json) => AssistantAction(
    kind: AssistantActionKind.values.firstWhere(
      (k) => k.name.toLowerCase() == (json['kind'] as String).toLowerCase(),
      orElse: () => AssistantActionKind.createChore,
    ),
    listId: json['listId'] as String?,
    listName: json['listName'] as String?,
    items: [for (final i in json['items'] as List<dynamic>? ?? const []) i as String],
    itemIds: [for (final i in json['itemIds'] as List<dynamic>? ?? const []) i as String],
    title: json['title'] as String?,
    date: _day(json['date']),
    endDate: _day(json['endDate']),
    startsAt: _instant(json['startsAt']),
    endsAt: _instant(json['endsAt']),
    amountCents: json['amountCents'] as int?,
    location: json['location'] as String?,
  );
}

/// O que o assistente propõe: uma frase para a pessoa e as ações (as que não se aproveitaram contam em [skipped]).
class AssistantProposal {
  const AssistantProposal({
    this.reply,
    required this.actions,
    required this.skipped,
    required this.usedToday,
    required this.dailyLimit,
  });

  final String? reply;
  final List<AssistantAction> actions;
  final int skipped;
  final int usedToday;
  final int dailyLimit;

  factory AssistantProposal.fromJson(Map<String, dynamic> json) => AssistantProposal(
    reply: json['reply'] as String?,
    actions: [
      for (final a in json['actions'] as List<dynamic>? ?? const [])
        AssistantAction.fromJson(a as Map<String, dynamic>),
    ],
    skipped: json['skipped'] as int? ?? 0,
    usedToday: json['usedToday'] as int? ?? 0,
    dailyLimit: json['dailyLimit'] as int? ?? 0,
  );
}
