import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/models.dart';
import '../notifications/notification_center.dart';
import '../notifications/push_registration.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import '../shopping/shopping_repository.dart';
import 'calendar_view.dart';
import 'chores_view.dart';
import 'devices_screen.dart';
import 'household_screen.dart';
import 'notifications_screen.dart';
import 'onboarding_screen.dart';
import 'rewards_view.dart';
import 'shopping_items_view.dart';
import 'shopping_lists_view.dart';
import 'theme.dart';

/// Ecrã principal de uma casa, com as secções Compras, Tarefas, Calendário e Recompensas.
/// Em ecrãs largos (PC, tablet) usa uma barra lateral e mostra listas e artigos lado a lado;
/// no telemóvel usa a barra inferior e abre cada lista num ecrã próprio.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});

  static const wideLayoutBreakpoint = 840.0;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  static const _sections = [
    (icon: Icons.shopping_cart_outlined, selectedIcon: Icons.shopping_cart, label: 'Compras'),
    (icon: Icons.task_alt_outlined, selectedIcon: Icons.task_alt, label: 'Tarefas'),
    (icon: Icons.calendar_month_outlined, selectedIcon: Icons.calendar_month, label: 'Calendário'),
    (icon: Icons.card_giftcard_outlined, selectedIcon: Icons.card_giftcard, label: 'Recompensas'),
  ];

  String? _connectedHouseholdId;
  var _pushRegistered = false;
  ShoppingList? _selectedList;
  int _section = 0;
  StreamSubscription<HouseholdEvent>? _membership;

  @override
  void initState() {
    super.initState();
    // Quem sai (ou é tirado) de uma casa deixa de a ver: abre-se outra, ou o ecrã de boas-vindas.
    _membership = context.read<HouseholdRealtime>().events.listen(_onEvent);
  }

  void _onEvent(HouseholdEvent event) {
    if (!mounted) return;
    if (event is! MembersChanged) return;
    final session = context.read<SessionController>();
    if (event.removedUserId != null && event.removedUserId == session.user?.id) {
      final name = session.households.where((h) => h.id == event.householdId).firstOrNull?.name;
      if (name != null) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Já não estás em $name.')));
      }
    }
    session.memberChanged(event.householdId, member: event.member, removedUserId: event.removedUserId);
  }

  @override
  void dispose() {
    _membership?.cancel();
    super.dispose();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final household = context.watch<SessionController>().currentHousehold;
    if (household != null && household.id != _connectedHouseholdId) {
      _connectedHouseholdId = household.id;
      _selectedList = null;
      context.read<HouseholdRealtime>().connect(household.id);
      context.read<NotificationCenter>().load();
      if (!_pushRegistered) {
        _pushRegistered = true;
        context.read<PushRegistration>().register();
      }
    }
  }

  Future<void> _signOut(SessionController session, {required bool everywhere}) async {
    if (everywhere) {
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Terminar sessão em todos os dispositivos?'),
          content: const Text(
            'Vais ter de entrar outra vez em todos os telemóveis e computadores. '
            'Útil se perdeste um telemóvel ou entraste num computador que não é teu.',
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancelar')),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Terminar sessões')),
          ],
        ),
      );
      if (confirmed != true || !mounted) return;
    }

    final realtime = context.read<HouseholdRealtime>();
    final shopping = context.read<ShoppingRepository>();
    final notifications = context.read<NotificationCenter>();
    final push = context.read<PushRegistration>();
    try {
      // Ainda com sessão: o telemóvel deixa de receber os avisos desta conta.
      await push.unregister();
      await (everywhere ? session.signOutEverywhere() : session.signOut());
    } catch (e) {
      if (mounted) showError(context, e);
      return;
    }
    await realtime.disconnect();
    await shopping.clear();
    notifications.clear();
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

        final shopping = wide
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
            : lists;
        final content = switch (_section) {
          0 => shopping,
          1 => ChoresView(key: ValueKey('chores-${household.id}'), household: household),
          2 => CalendarView(key: ValueKey('calendar-${household.id}'), household: household),
          _ => RewardsView(key: ValueKey('rewards-${household.id}'), household: household),
        };

        return Scaffold(
          appBar: AppBar(
            title: _HouseholdSwitcher(session: session, current: household),
            actions: [
              const NotificationsBell(),
              IconButton(
                tooltip: 'Membros e convites',
                icon: const Icon(Icons.group_outlined),
                onPressed: () =>
                    Navigator.of(context)
                        .push(MaterialPageRoute<void>(builder: (_) => HouseholdScreen(householdId: household.id))),
              ),
              PopupMenuButton<String>(
                onSelected: (value) => value == 'devices'
                    ? Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const DevicesScreen()))
                    : _signOut(session, everywhere: value == 'logout-all'),
                itemBuilder: (_) => [
                  PopupMenuItem(enabled: false, child: Text(session.user?.email ?? session.user?.displayName ?? '')),
                  const PopupMenuItem(value: 'devices', child: Text('Dispositivos com sessão iniciada')),
                  const PopupMenuItem(value: 'logout', child: Text('Terminar sessão')),
                  const PopupMenuItem(value: 'logout-all', child: Text('Terminar sessão em todos os dispositivos')),
                ],
              ),
            ],
          ),
          body: wide
              ? Row(
                  children: [
                    NavigationRail(
                      selectedIndex: _section,
                      onDestinationSelected: (i) => setState(() => _section = i),
                      labelType: NavigationRailLabelType.all,
                      destinations: [
                        for (final s in _sections)
                          NavigationRailDestination(
                            icon: Icon(s.icon),
                            selectedIcon: Icon(s.selectedIcon),
                            label: Text(s.label),
                          ),
                      ],
                    ),
                    const VerticalDivider(width: 1),
                    Expanded(child: content),
                  ],
                )
              : content,
          bottomNavigationBar: wide
              ? null
              : NavigationBar(
                  selectedIndex: _section,
                  onDestinationSelected: (i) => setState(() => _section = i),
                  destinations: [
                    for (final s in _sections)
                      NavigationDestination(icon: Icon(s.icon), selectedIcon: Icon(s.selectedIcon), label: s.label),
                  ],
                ),
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
        // Os perfis de criança só existem na sua casa.
        if (session.user?.isChildProfile != true) ...[
          const PopupMenuDivider(),
          const PopupMenuItem(value: '_add', child: Text('Adicionar casa…')),
        ],
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
