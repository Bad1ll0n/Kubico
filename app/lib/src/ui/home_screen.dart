import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/models.dart';
import '../l10n/strings.dart';
import '../notifications/notification_center.dart';
import '../notifications/push_registration.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import '../shopping/shopping_repository.dart';
import 'account_screen.dart';
import 'activity_screen.dart';
import 'calendar_view.dart';
import 'chores_view.dart';
import 'devices_screen.dart';
import 'help_screen.dart';
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
    (module: HouseholdModule.shopping, icon: Icons.shopping_cart_outlined, selectedIcon: Icons.shopping_cart),
    (module: HouseholdModule.chores, icon: Icons.task_alt_outlined, selectedIcon: Icons.task_alt),
    (module: HouseholdModule.calendar, icon: Icons.calendar_month_outlined, selectedIcon: Icons.calendar_month),
    (module: HouseholdModule.rewards, icon: Icons.card_giftcard_outlined, selectedIcon: Icons.card_giftcard),
  ];

  String? _connectedHouseholdId;
  var _pushRegistered = false;
  ShoppingList? _selectedList;
  HouseholdModule _section = HouseholdModule.shopping;
  StreamSubscription<HouseholdEvent>? _membership;

  @override
  void initState() {
    super.initState();
    // Quem sai (ou é tirado) de uma casa deixa de a ver: abre-se outra, ou o ecrã de boas-vindas.
    _membership = context.read<HouseholdRealtime>().events.listen(_onEvent);
    WidgetsBinding.instance.addPostFrameCallback((_) => _offerPendingInvite());
  }

  /// Chegou-se por um link de convite já com casas: pergunta se quer entrar na casa do convite.
  Future<void> _offerPendingInvite() async {
    final session = context.read<SessionController>();
    final code = session.pendingInvite;
    if (code == null) return;
    session.pendingInvite = null;
    try {
      final invite = await session.api.previewInvite(code);
      if (!mounted) return;
      if (invite.alreadyMember) {
        await session.selectHousehold(invite.householdId);
        return;
      }

      final join = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text('Entrar em ${invite.householdName}?'.tr),
          content: Text('Recebeste um convite para esta casa, com o papel de ${invite.role.label}.'.tr),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Agora não'.tr)),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: Text('Entrar'.tr)),
          ],
        ),
      );
      if (join == true) await session.joinHousehold(code);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  void _onEvent(HouseholdEvent event) {
    if (!mounted) return;
    final session = context.read<SessionController>();
    if (event is PermissionsChanged) {
      // As áreas que deixei de ver desaparecem; as novas aparecem.
      session.refresh().ignore();
      return;
    }
    if (event is HouseholdDeleted) {
      // Saiu o último membro com conta (noutro aparelho): a casa já não existe.
      final name = event.name.isNotEmpty
          ? event.name
          : session.households.where((h) => h.id == event.householdId).firstOrNull?.name ?? '';
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('A casa «$name» foi apagada.'.tr)));
      session.householdLost(event.householdId).ignore();
      return;
    }
    if (event is! MembersChanged) return;
    if (event.removedUserId != null && event.removedUserId == session.user?.id) {
      final name = session.households.where((h) => h.id == event.householdId).firstOrNull?.name;
      if (name != null) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Já não estás em $name.'.tr)));
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
    var resetFeeds = false;
    if (everywhere) {
      final answer = await showDialog<bool>(context: context, builder: (_) => const _SignOutEverywhereDialog());
      if (answer == null || !mounted) return;
      resetFeeds = answer;
    }

    final realtime = context.read<HouseholdRealtime>();
    final shopping = context.read<ShoppingRepository>();
    final notifications = context.read<NotificationCenter>();
    final push = context.read<PushRegistration>();
    try {
      // Ainda com sessão: o telemóvel deixa de receber os avisos desta conta.
      await push.unregister();
      await (everywhere ? session.signOutEverywhere(resetCalendarFeeds: resetFeeds) : session.signOut());
    } catch (e) {
      if (mounted) showError(context, e);
      return;
    }
    await realtime.disconnect();
    await shopping.clear();
    notifications.clear();
  }

  /// A minha conta. Se for apagada, arruma o que a app guardou desta sessão (como ao terminar sessão).
  Future<void> _openAccount() {
    final realtime = context.read<HouseholdRealtime>();
    final shopping = context.read<ShoppingRepository>();
    final notifications = context.read<NotificationCenter>();
    return Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => AccountScreen(
          onDeleted: () async {
            await realtime.disconnect();
            await shopping.clear();
            notifications.clear();
          },
        ),
      ),
    );
  }

  void _openList(ShoppingList list, bool wide) {
    if (wide) {
      setState(() => _selectedList = list);
      return;
    }
    final access = context.read<SessionController>().currentHousehold?.accessTo(HouseholdModule.shopping);
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => Scaffold(
          appBar: AppBar(title: Text(list.name)),
          body: ShoppingItemsView(householdId: list.householdId, list: list, canEdit: access?.canParticipate ?? true),
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
                            canEdit: household.accessTo(HouseholdModule.shopping).canParticipate,
                          ),
                  ),
                ],
              )
            : lists;
        // Só aparecem as áreas a que o meu papel dá acesso nesta casa.
        final sections = [
          for (final s in _sections)
            if (household.accessTo(s.module).canView) s,
        ];
        final section = sections.where((s) => s.module == _section).firstOrNull ?? sections.firstOrNull;
        // Se a área aberta deixou de se ver, fica-se na que a substituiu (mesmo que a outra volte).
        if (section != null) _section = section.module;
        final index = section == null ? 0 : sections.indexOf(section);
        void select(int i) => setState(() => _section = sections[i].module);
        final Widget content = switch (section?.module) {
          null => const _NoAreas(),
          HouseholdModule.shopping => shopping,
          HouseholdModule.chores => ChoresView(key: ValueKey('chores-${household.id}'), household: household),
          HouseholdModule.calendar => CalendarView(key: ValueKey('calendar-${household.id}'), household: household),
          HouseholdModule.rewards => RewardsView(key: ValueKey('rewards-${household.id}'), household: household),
        };
        // As barras de navegação precisam de pelo menos duas áreas.
        final navigation = sections.length >= 2;

        return Scaffold(
          appBar: AppBar(
            title: _HouseholdSwitcher(session: session, current: household),
            actions: [
              const NotificationsBell(),
              IconButton(
                tooltip: 'Membros e convites'.tr,
                icon: const Icon(Icons.group_outlined),
                onPressed: () =>
                    Navigator.of(context)
                        .push(MaterialPageRoute<void>(builder: (_) => HouseholdScreen(householdId: household.id))),
              ),
              PopupMenuButton<String>(
                onSelected: (value) => switch (value) {
                  'devices' => Navigator.of(
                    context,
                  ).push(MaterialPageRoute<void>(builder: (_) => const DevicesScreen())),
                  'account' => _openAccount(),
                  'activity' => Navigator.of(
                    context,
                  ).push(MaterialPageRoute<void>(builder: (_) => ActivityScreen(household: household))),
                  'help' => Navigator.of(
                    context,
                  ).push(MaterialPageRoute<void>(builder: (_) => HelpScreen(householdId: household.id))),
                  _ => _signOut(session, everywhere: value == 'logout-all'),
                },
                itemBuilder: (_) => [
                  PopupMenuItem(enabled: false, child: Text(session.user?.email ?? session.user?.displayName ?? '')),
                  PopupMenuItem(value: 'activity', child: Text('Histórico de alterações'.tr)),
                  PopupMenuItem(value: 'account', child: Text('A minha conta'.tr)),
                  PopupMenuItem(value: 'devices', child: Text('Dispositivos com sessão iniciada'.tr)),
                  PopupMenuItem(value: 'help', child: Text('Ajuda e sugestões'.tr)),
                  PopupMenuItem(value: 'logout', child: Text('Terminar sessão'.tr)),
                  PopupMenuItem(value: 'logout-all', child: Text('Terminar sessão em todos os dispositivos'.tr)),
                ],
              ),
            ],
          ),
          body: wide && navigation
              ? Row(
                  children: [
                    NavigationRail(
                      selectedIndex: index,
                      onDestinationSelected: select,
                      labelType: NavigationRailLabelType.all,
                      destinations: [
                        for (final s in sections)
                          NavigationRailDestination(
                            icon: Icon(s.icon),
                            selectedIcon: Icon(s.selectedIcon),
                            label: Text(s.module.label),
                          ),
                      ],
                    ),
                    const VerticalDivider(width: 1),
                    Expanded(child: content),
                  ],
                )
              : content,
          bottomNavigationBar: wide || !navigation
              ? null
              : NavigationBar(
                  selectedIndex: index,
                  onDestinationSelected: select,
                  destinations: [
                    for (final s in sections)
                      NavigationDestination(
                        icon: Icon(s.icon),
                        selectedIcon: Icon(s.selectedIcon),
                        label: s.module.label,
                      ),
                  ],
                ),
        );
      },
    );
  }
}

/// Confirmar "terminar sessão em todos os dispositivos", com a opção de gerar também endereços novos para o
/// calendário subscrito (o de um telemóvel perdido continuaria a mostrar os eventos). Devolve essa escolha.
class _SignOutEverywhereDialog extends StatefulWidget {
  const _SignOutEverywhereDialog();

  @override
  State<_SignOutEverywhereDialog> createState() => _SignOutEverywhereDialogState();
}

class _SignOutEverywhereDialogState extends State<_SignOutEverywhereDialog> {
  var _resetFeeds = false;

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text('Terminar sessão em todos os dispositivos?'.tr),
    content: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          'Vais ter de entrar outra vez em todos os telemóveis e computadores. '
                  'Útil se perdeste um telemóvel ou entraste num computador que não é teu.'
              .tr,
        ),
        CheckboxListTile(
          contentPadding: EdgeInsets.zero,
          value: _resetFeeds,
          onChanged: (value) => setState(() => _resetFeeds = value ?? false),
          title: Text('Gerar também endereços novos para o calendário subscrito'.tr),
          subtitle: Text('Os calendários que o subscreveram deixam de receber os eventos.'.tr),
        ),
      ],
    ),
    actions: [
      TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
      FilledButton(onPressed: () => Navigator.pop(context, _resetFeeds), child: Text('Terminar sessões'.tr)),
    ],
  );
}

class _HouseholdSwitcher extends StatelessWidget {
  const _HouseholdSwitcher({required this.session, required this.current});

  final SessionController session;
  final HouseholdSummary current;

  @override
  Widget build(BuildContext context) {
    return PopupMenuButton<String>(
      tooltip: 'Mudar de casa'.tr,
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
          PopupMenuItem(value: '_add', child: Text('Adicionar casa…'.tr)),
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

/// O administrador tirou o acesso a todas as áreas a este papel.
class _NoAreas extends StatelessWidget {
  const _NoAreas();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.lock_outline, size: 48, color: theme.colorScheme.outline),
            const SizedBox(height: 12),
            Text(
              'Ainda não tens nada para ver nesta casa'.tr,
              style: theme.textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 4),
            Text('Pede ao administrador da casa para te dar acesso.'.tr, textAlign: TextAlign.center),
          ],
        ),
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
          Text('Escolhe uma lista'.tr, style: theme.textTheme.titleMedium),
        ],
      ),
    );
  }
}
