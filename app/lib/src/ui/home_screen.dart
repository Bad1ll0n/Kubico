import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/models.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'household_screen.dart';
import 'onboarding_screen.dart';
import 'shopping_items_view.dart';
import 'shopping_lists_view.dart';

/// Ecrã principal de uma casa. Em ecrãs largos (PC, tablet) mostra as listas e os
/// artigos lado a lado; no telemóvel abre cada lista num ecrã próprio.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});

  static const wideLayoutBreakpoint = 840.0;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  String? _connectedHouseholdId;
  ShoppingList? _selectedList;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final household = context.watch<SessionController>().currentHousehold;
    if (household != null && household.id != _connectedHouseholdId) {
      _connectedHouseholdId = household.id;
      _selectedList = null;
      context.read<HouseholdRealtime>().connect(household.id);
    }
  }

  void _openList(ShoppingList list, bool wide) {
    if (wide) {
      setState(() => _selectedList = list);
      return;
    }
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => Scaffold(
          appBar: AppBar(title: Text(list.name)),
          body: ShoppingItemsView(householdId: list.householdId, list: list),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final session = context.watch<SessionController>();
    final household = session.currentHousehold!;

    return LayoutBuilder(
      builder: (context, constraints) {
        final wide = constraints.maxWidth >= HomeScreen.wideLayoutBreakpoint;
        final lists = ShoppingListsView(
          key: ValueKey(household.id),
          household: household,
          selectedListId: wide ? _selectedList?.id : null,
          onOpen: (list) => _openList(list, wide),
          onDeleted: (listId) {
            if (_selectedList?.id == listId) setState(() => _selectedList = null);
          },
        );

        return Scaffold(
          appBar: AppBar(
            title: _HouseholdSwitcher(session: session, current: household),
            actions: [
              IconButton(
                tooltip: 'Membros e convites',
                icon: const Icon(Icons.group_outlined),
                onPressed: () =>
                    Navigator.of(context)
                        .push(MaterialPageRoute<void>(builder: (_) => HouseholdScreen(householdId: household.id))),
              ),
              PopupMenuButton<String>(
                onSelected: (value) async {
                  if (value == 'logout') {
                    await context.read<HouseholdRealtime>().disconnect();
                    await session.signOut();
                  }
                },
                itemBuilder: (_) => [
                  PopupMenuItem(enabled: false, child: Text(session.user?.email ?? '')),
                  const PopupMenuItem(value: 'logout', child: Text('Terminar sessão')),
                ],
              ),
            ],
          ),
          body: wide
              ? Row(
                  children: [
                    SizedBox(width: 340, child: lists),
                    const VerticalDivider(width: 1),
                    Expanded(
                      child: _selectedList == null
                          ? const _EmptyDetail()
                          : ShoppingItemsView(
                              key: ValueKey(_selectedList!.id),
                              householdId: household.id,
                              list: _selectedList!,
                              showTitle: true,
                            ),
                    ),
                  ],
                )
              : lists,
        );
      },
    );
  }
}

class _HouseholdSwitcher extends StatelessWidget {
  const _HouseholdSwitcher({required this.session, required this.current});

  final SessionController session;
  final HouseholdSummary current;

  @override
  Widget build(BuildContext context) {
    return PopupMenuButton<String>(
      tooltip: 'Mudar de casa',
      onSelected: (id) {
        if (id == '_add') {
          Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const OnboardingScreen(canGoBack: true)));
        } else {
          session.selectHousehold(id);
        }
      },
      itemBuilder: (_) => [
        for (final h in session.households)
          CheckedPopupMenuItem(value: h.id, checked: h.id == current.id, child: Text(h.name)),
        const PopupMenuDivider(),
        const PopupMenuItem(value: '_add', child: Text('Adicionar casa…')),
      ],
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.cottage_outlined),
          const SizedBox(width: 8),
          Flexible(child: Text(current.name, overflow: TextOverflow.ellipsis)),
          const Icon(Icons.arrow_drop_down),
        ],
      ),
    );
  }
}

class _EmptyDetail extends StatelessWidget {
  const _EmptyDetail();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.shopping_basket_outlined, size: 48, color: theme.colorScheme.outline),
          const SizedBox(height: 12),
          Text('Escolhe uma lista', style: theme.textTheme.titleMedium),
        ],
      ),
    );
  }
}
