import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import 'dates_pt.dart';
import 'design/components.dart';
import 'shopping_items_view.dart' show parseItemInput;
import 'theme.dart';

/// Refeições (F4-09): o plano da semana (almoço e jantar) e as receitas da casa. Os ingredientes das receitas
/// planeadas vão para a lista de compras com um toque.
class MealsView extends StatefulWidget {
  const MealsView({required this.household, super.key});

  final HouseholdSummary household;

  @override
  State<MealsView> createState() => _MealsViewState();
}

class _MealsViewState extends State<MealsView> {
  late DateTime _monday = _mondayOf(clock.now());
  List<Recipe>? _recipes;
  Map<(DateTime, MealSlot), MealPlanEntry> _plan = {};
  Object? _error;
  StreamSubscription<HouseholdEvent>? _events;
  late final _reload = CoalescedReload(_load);

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;
  ModuleAccess get _access => widget.household.accessTo(HouseholdModule.meals);

  static DateTime _mondayOf(DateTime day) => DateTime(day.year, day.month, day.day - day.weekday + 1);

  List<DateTime> get _days => [for (var i = 0; i < 7; i++) DateTime(_monday.year, _monday.month, _monday.day + i)];

  @override
  void initState() {
    super.initState();
    _events = context.read<HouseholdRealtime>().events.listen((event) {
      if (event is MealsChanged) _reload.request();
    });
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    _reload.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final days = _days;
      final results = await Future.wait<Object>([
        _api.getRecipes(_householdId),
        _api.getMealPlan(_householdId, days.first, days.last),
      ]);
      if (!mounted) return;
      setState(() {
        _recipes = results[0] as List<Recipe>;
        _plan = {
          for (final e in results[1] as List<MealPlanEntry>)
            if (!e.isEmpty) (DateUtils.dateOnly(e.date), e.slot): e,
        };
        _error = null;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  void _week(int delta) {
    setState(() => _monday = DateTime(_monday.year, _monday.month, _monday.day + 7 * delta));
    _load();
  }

  Recipe? _recipe(String? id) => _recipes?.where((r) => r.id == id).firstOrNull;

  /// Escolher o que se come: uma receita, outra coisa (texto) ou nada.
  Future<void> _choose(DateTime day, MealSlot slot) async {
    final current = _plan[(day, slot)];
    final choice = await showModalBottomSheet<_MealChoice>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (context) =>
          _MealPicker(title: '${slot.label} · ${formatLongDate(day)}', recipes: _recipes ?? const [], current: current),
    );
    if (choice == null || !mounted) return;
    try {
      final saved = await _api.saveMeal(_householdId, day, slot, recipeId: choice.recipeId, text: choice.text);
      if (!mounted) return;
      setState(() => saved.isEmpty ? _plan.remove((day, slot)) : _plan[(day, slot)] = saved);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  /// Os ingredientes da semana vão para uma lista de compras.
  Future<void> _toList() async {
    final List<ShoppingList> lists;
    try {
      lists = await _api.getLists(_householdId);
    } catch (e) {
      if (mounted) showError(context, e);
      return;
    }
    if (!mounted) return;
    if (lists.isEmpty) {
      showError(context, 'Cria primeiro uma lista de compras.'.tr);
      return;
    }
    final list = lists.length == 1
        ? lists.single
        : await showDialog<ShoppingList>(
            context: context,
            builder: (context) => SimpleDialog(
              title: Text('Para que lista?'.tr),
              children: [
                for (final l in lists)
                  SimpleDialogOption(onPressed: () => Navigator.pop(context, l), child: Text(l.name)),
              ],
            ),
          );
    if (list == null || !mounted) return;
    try {
      final days = _days;
      final names = await _api.mealsToList(_householdId, days.first, days.last, list.id);
      if (!mounted) return;
      final added = names.join(', ');
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              names.isEmpty
                  ? 'Já está tudo na lista «${list.name}».'.tr
                  : 'Foram para a lista «${list.name}»: $added.'.tr,
            ),
          ),
        );
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _editRecipe([Recipe? recipe]) async {
    final result = await Navigator.of(context)
        .push<_RecipeResult>(MaterialPageRoute(fullscreenDialog: true, builder: (_) => _RecipeEditor(recipe: recipe)));
    if (result == null || !mounted) return;
    try {
      if (result.delete) {
        await _api.deleteRecipe(_householdId, recipe!.id);
      } else {
        await _api.saveRecipe(_householdId, result.draft!, recipeId: recipe?.id);
      }
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final recipes = _recipes;
    if (recipes == null) {
      return _error == null
          ? const Center(child: CircularProgressIndicator())
          : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load);
    }
    return DefaultTabController(
      length: 2,
      child: Column(
        children: [
          TabBar(
            tabs: [
              Tab(text: 'Semana'.tr),
              Tab(text: 'Receitas'.tr),
            ],
          ),
          Expanded(child: TabBarView(children: [_weekView(), _recipesView(recipes)])),
        ],
      ),
    );
  }

  Widget _weekView() {
    final theme = Theme.of(context);
    final days = _days;
    final today = DateUtils.dateOnly(clock.now());
    final canPlan = _access.canParticipate;
    final canShop = widget.household.accessTo(HouseholdModule.shopping).canParticipate;
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
        children: [
          Row(
            children: [
              IconButton(
                tooltip: 'Semana anterior'.tr,
                onPressed: () => _week(-1),
                icon: const Icon(Icons.chevron_left_rounded),
              ),
              Expanded(
                child: Text(
                  '${formatDayMonth(days.first)} – ${formatDayMonth(days.last)}',
                  textAlign: TextAlign.center,
                  style: theme.textTheme.titleMedium,
                ),
              ),
              IconButton(
                tooltip: 'Semana seguinte'.tr,
                onPressed: () => _week(1),
                icon: const Icon(Icons.chevron_right_rounded),
              ),
            ],
          ),
          if (canShop && _plan.values.any((e) => e.recipeId != null))
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: FilledButton.tonalIcon(
                onPressed: _toList,
                icon: const Icon(Icons.add_shopping_cart_rounded),
                label: Text('Pôr os ingredientes na lista'.tr),
              ),
            ),
          for (final day in days)
            Card(
              color: day == today ? theme.colorScheme.primaryContainer.withValues(alpha: 0.4) : null,
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Padding(
                      padding: const EdgeInsets.fromLTRB(16, 8, 16, 0),
                      child: Text(capitalize(formatLongDate(day)), style: theme.textTheme.titleSmall),
                    ),
                    for (final slot in MealSlot.values)
                      ListTile(
                        dense: true,
                        leading: Icon(slot == MealSlot.lunch ? Icons.wb_sunny_rounded : Icons.nightlight_round_rounded),
                        title: Text(_describe(_plan[(day, slot)])),
                        subtitle: Text(slot.label),
                        onTap: canPlan ? () => _choose(day, slot) : null,
                        trailing: canPlan ? const Icon(Icons.edit_rounded, size: 18) : null,
                      ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }

  String _describe(MealPlanEntry? entry) {
    if (entry == null || entry.isEmpty) return '—';
    return _recipe(entry.recipeId)?.title ?? entry.text ?? '—';
  }

  Widget _recipesView(List<Recipe> recipes) {
    final canManage = _access.canManage;
    return Scaffold(
      floatingActionButton: canManage
          ? FloatingActionButton.extended(
              onPressed: _editRecipe,
              icon: const Icon(Icons.add_rounded),
              label: Text('Nova receita'.tr),
            )
          : null,
      body: recipes.isEmpty
          ? StateView(
              icon: Icons.restaurant_menu_rounded,
              color: context.kubico.area(KubicoArea.shopping).color,
              title: 'Ainda não há receitas'.tr,
              message: canManage
                  ? 'Guarda as receitas da casa: os ingredientes vão para a lista com um toque.'.tr
                  : null,
            )
          : ListView.builder(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
              itemCount: recipes.length,
              itemBuilder: (context, index) {
                final r = recipes[index];
                return Card(
                  child: ListTile(
                    leading: const Icon(Icons.restaurant_menu_rounded),
                    title: Text(r.title),
                    subtitle: Text(
                      [
                        if (r.tags.isNotEmpty) r.tags.join(', '),
                        r.ingredients.length == 1 ? '1 ingrediente'.tr : '${r.ingredients.length} ingredientes'.tr,
                      ].join(' · '),
                    ),
                    onTap: () => canManage ? _editRecipe(r) : _showRecipe(r),
                  ),
                );
              },
            ),
    );
  }

  void _showRecipe(Recipe r) => showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (context) => DraggableScrollableSheet(
      expand: false,
      builder: (context, controller) => ListView(
        controller: controller,
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 24),
        children: [
          Text(r.title, style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 12),
          Text('Ingredientes'.tr, style: Theme.of(context).textTheme.titleSmall),
          for (final i in r.ingredients) Text('• $i'),
          if (r.steps.isNotEmpty) ...[
            const SizedBox(height: 12),
            Text('Passos'.tr, style: Theme.of(context).textTheme.titleSmall),
            for (final (n, step) in r.steps.indexed) Text('${n + 1}. $step'),
          ],
        ],
      ),
    ),
  );
}

class _MealChoice {
  const _MealChoice({this.recipeId, this.text});

  final String? recipeId;
  final String? text;
}

/// Escolher a receita (com pesquisa), escrever outra coisa ou deixar o dia vazio.
class _MealPicker extends StatefulWidget {
  const _MealPicker({required this.title, required this.recipes, required this.current});

  final String title;
  final List<Recipe> recipes;
  final MealPlanEntry? current;

  @override
  State<_MealPicker> createState() => _MealPickerState();
}

class _MealPickerState extends State<_MealPicker> {
  final _search = TextEditingController();

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final query = _search.text.trim().toLowerCase();
    final matches = [
      for (final r in widget.recipes)
        if (query.isEmpty ||
            r.title.toLowerCase().contains(query) ||
            r.tags.any((t) => t.toLowerCase().contains(query)))
          r,
    ];
    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: SizedBox(
        height: MediaQuery.sizeOf(context).height * 0.75,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
              child: Text(widget.title, style: Theme.of(context).textTheme.titleMedium),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: TextField(
                controller: _search,
                decoration: InputDecoration(
                  hintText: 'Procurar ou escrever outra coisa'.tr,
                  prefixIcon: const Icon(Icons.search_rounded),
                ),
                onChanged: (_) => setState(() {}),
                onSubmitted: (text) {
                  if (text.trim().isNotEmpty) Navigator.pop(context, _MealChoice(text: text.trim()));
                },
              ),
            ),
            Expanded(
              child: ListView(
                children: [
                  if (query.isNotEmpty)
                    ListTile(
                      leading: const Icon(Icons.edit_note_rounded),
                      title: Text('Usar «${_search.text.trim()}»'.tr),
                      onTap: () => Navigator.pop(context, _MealChoice(text: _search.text.trim())),
                    ),
                  if (widget.current != null && !widget.current!.isEmpty)
                    ListTile(
                      leading: const Icon(Icons.clear_rounded),
                      title: Text('Deixar vazio'.tr),
                      onTap: () => Navigator.pop(context, const _MealChoice()),
                    ),
                  for (final r in matches)
                    ListTile(
                      leading: const Icon(Icons.restaurant_menu_rounded),
                      title: Text(r.title),
                      selected: widget.current?.recipeId == r.id,
                      onTap: () => Navigator.pop(context, _MealChoice(recipeId: r.id)),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _RecipeResult {
  const _RecipeResult.save(RecipeDraft this.draft) : delete = false;
  const _RecipeResult.delete() : draft = null, delete = true;

  final RecipeDraft? draft;
  final bool delete;
}

/// Criar ou mudar uma receita: um ingrediente por linha ("2 cebolas"), um passo por linha, etiquetas por vírgulas.
class _RecipeEditor extends StatefulWidget {
  const _RecipeEditor({this.recipe});

  final Recipe? recipe;

  @override
  State<_RecipeEditor> createState() => _RecipeEditorState();
}

class _RecipeEditorState extends State<_RecipeEditor> {
  late final _title = TextEditingController(text: widget.recipe?.title ?? '');
  late final _ingredients = TextEditingController(text: widget.recipe?.ingredients.join('\n') ?? '');
  late final _steps = TextEditingController(text: widget.recipe?.steps.join('\n') ?? '');
  late final _tags = TextEditingController(text: widget.recipe?.tags.join(', ') ?? '');
  late int _servings = widget.recipe?.servings ?? 4;

  @override
  void dispose() {
    for (final c in [_title, _ingredients, _steps, _tags]) {
      c.dispose();
    }
    super.dispose();
  }

  List<String> _lines(TextEditingController c) => [
    for (final line in c.text.split('\n'))
      if (line.trim().isNotEmpty) line.trim(),
  ];

  void _save() {
    final title = _title.text.trim();
    if (title.isEmpty) return;
    // Mantém a secção dos ingredientes que já existiam (a lista de compras usa-a).
    final known = {for (final i in widget.recipe?.ingredients ?? const <RecipeIngredient>[]) i.name.toLowerCase(): i};
    final ingredients = [
      for (final line in _lines(_ingredients))
        () {
          final (name, quantity) = parseItemInput(line);
          return RecipeIngredient(name: name, quantity: quantity, category: known[name.toLowerCase()]?.category);
        }(),
    ];
    Navigator.pop(
      context,
      _RecipeResult.save(
        RecipeDraft(
          title: title,
          servings: _servings,
          ingredients: ingredients,
          steps: _lines(_steps),
          tags: [
            for (final t in _tags.text.split(','))
              if (t.trim().isNotEmpty) t.trim(),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.recipe == null ? 'Nova receita'.tr : 'Editar receita'.tr),
        actions: [
          if (widget.recipe != null)
            IconButton(
              tooltip: 'Apagar receita'.tr,
              icon: const Icon(Icons.delete_outline_rounded),
              onPressed: () => Navigator.pop(context, const _RecipeResult.delete()),
            ),
          TextButton(onPressed: _save, child: Text('Guardar'.tr)),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          TextField(
            controller: _title,
            autofocus: widget.recipe == null,
            textCapitalization: TextCapitalization.sentences,
            decoration: InputDecoration(labelText: 'Nome'.tr),
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(child: Text('Para $_servings pessoas'.tr)),
              IconButton(
                tooltip: 'Menos pessoas'.tr,
                onPressed: _servings > 1 ? () => setState(() => _servings--) : null,
                icon: const Icon(Icons.remove_rounded),
              ),
              IconButton(
                tooltip: 'Mais pessoas'.tr,
                onPressed: _servings < 50 ? () => setState(() => _servings++) : null,
                icon: const Icon(Icons.add_rounded),
              ),
            ],
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _ingredients,
            minLines: 4,
            maxLines: null,
            decoration: InputDecoration(
              labelText: 'Ingredientes (um por linha)'.tr,
              hintText: '2 cebolas\n500 g de carne picada'.tr,
              alignLabelWithHint: true,
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _steps,
            minLines: 3,
            maxLines: null,
            decoration: InputDecoration(labelText: 'Passos (um por linha)'.tr, alignLabelWithHint: true),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _tags,
            decoration: InputDecoration(
              labelText: 'Etiquetas (separadas por vírgulas)'.tr,
              hintText: 'Rápido, Peixe'.tr,
            ),
          ),
        ],
      ),
    );
  }
}
