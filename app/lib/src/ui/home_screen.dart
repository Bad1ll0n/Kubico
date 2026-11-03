import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../notifications/notification_center.dart';
import '../notifications/open_notification.dart';
import '../notifications/push_registration.dart';
import '../realtime/household_realtime.dart';
import '../session/child_device.dart';
import '../session/session_controller.dart';
import '../shopping/shopping_repository.dart';
import 'account_screen.dart' deferred as account;
import 'activity_screen.dart' deferred as activity;
import 'calendar_view.dart';
import 'chores_view.dart';
import 'deferred.dart';
import 'devices_screen.dart' deferred as devices;
import 'help_screen.dart' deferred as help;
import 'household_screen.dart';
import 'kitchen_panel.dart' deferred as kitchen;
import 'expenses_view.dart';
import 'home_area_screen.dart' deferred as homearea;
import 'meals_view.dart';
import 'notifications_screen.dart';
import 'onboarding_screen.dart';
import 'rewards_view.dart';
import 'shopping_items_view.dart';
import 'shopping_lists_view.dart';
import 'theme.dart';
import 'today_view.dart';

/// Ecrã principal de uma casa: abre no Hoje (o resumo do dia) e tem as secções Compras, Tarefas, Calendário e
/// Recompensas.
/// Em ecrãs largos (PC, tablet) usa uma barra lateral e mostra listas e artigos lado a lado;
/// no telemóvel usa a barra inferior e abre cada lista num ecrã próprio.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});

  static const wideLayoutBreakpoint = 840.0;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  // module null é o Hoje.
  static const _sections = <({HouseholdModule? module, IconData icon, IconData selectedIcon, KubicoArea area})>[
    (module: null, icon: Icons.home_outlined, selectedIcon: Icons.home_rounded, area: KubicoArea.today),
    (
      module: HouseholdModule.shopping,
      icon: Icons.shopping_cart_outlined,
      selectedIcon: Icons.shopping_cart_rounded,
      area: KubicoArea.shopping,
    ),
    (
      module: HouseholdModule.chores,
      icon: Icons.check_circle_outline_rounded,
      selectedIcon: Icons.check_circle_rounded,
      area: KubicoArea.chores,
    ),
    (
      module: HouseholdModule.calendar,
      icon: Icons.calendar_month_outlined,
      selectedIcon: Icons.calendar_month_rounded,
      area: KubicoArea.calendar,
    ),
    (
      module: HouseholdModule.rewards,
      icon: Icons.star_outline_rounded,
      selectedIcon: Icons.star_rounded,
      area: KubicoArea.rewards,
    ),
  ];

  String? _connectedHouseholdId;
  var _pushRegistered = false;
  ShoppingList? _selectedList;
  HouseholdModule? _section;
  StreamSubscription<HouseholdEvent>? _membership;

  @override
  void initState() {
    super.initState();
    // Quem sai (ou é tirado) de uma casa deixa de a ver: abre-se outra, ou o ecrã de boas-vindas.
    _membership = context.read<HouseholdRealtime>().events.listen(_onEvent);
    OpenNotification.requested.addListener(_openNotification);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _offerPendingInvite();
      // Um aviso do telemóvel pode ter aberto a app antes de este ecrã existir.
      _openNotification();
    });
  }

  /// Tocou-se num aviso: muda para a casa dele e abre a área (ou os membros).
  Future<void> _openNotification() async {
    final target = OpenNotification.requested.value;
    if (target == null || !mounted) return;
    OpenNotification.requested.value = null;
    final session = context.read<SessionController>();
    if (!session.households.any((h) => h.id == target.householdId)) return;
    await session.selectHousehold(target.householdId);
    if (!mounted) return;
    // Os avisos da Casa abrem o ecrã "A casa" por cima do Hoje.
    if (target.module == HouseholdModule.home) {
      final household = session.households.firstWhere((h) => h.id == target.householdId);
      setState(() => _section = null);
      await pushDeferred<void>(context, homearea.loadLibrary, () => homearea.HomeAreaScreen(household: household));
      return;
    }
    setState(() {
      _section = target.module;
      _selectedList = null;
    });
    if (target.members) {
      await Navigator.of(context)
          .push(MaterialPageRoute<void>(builder: (_) => HouseholdScreen(householdId: target.householdId)));
    }
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
    OpenNotification.requested.removeListener(_openNotification);
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

  /// "Mudar de criança" (tablet partilhado): termina esta sessão e abre logo o "Quem és tu?".
  Future<void> _switchChild(SessionController session) {
    ChildDevice.openPickerOnSignIn = true;
    return _signOut(session, everywhere: false);
  }

  /// A minha conta. Se for apagada, arruma o que a app guardou desta sessão (como ao terminar sessão).
  Future<void> _openAccount() {
    final realtime = context.read<HouseholdRealtime>();
    final shopping = context.read<ShoppingRepository>();
    final notifications = context.read<NotificationCenter>();
    return pushDeferred<void>(
      context,
      account.loadLibrary,
      () => account.AccountScreen(
        onDeleted: () async {
          await realtime.disconnect();
          await shopping.clear();
          notifications.clear();
        },
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

        final canShop = household.accessTo(HouseholdModule.shopping).canView;
        final canEat = household.accessTo(HouseholdModule.meals).canView;
        final canPay = household.accessTo(HouseholdModule.expenses).canView;
        final Widget shoppingLists = wide
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
        // As Refeições vivem nas Compras (o plano da semana alimenta a lista), num separador próprio (F4-09).
        // E as Despesas (F4-10), também aqui: o supermercado é a despesa mais comum.
        final tabs = <(IconData, String, Widget)>[
          if (canShop) (Icons.checklist_rounded, 'Listas'.tr, shoppingLists),
          if (canEat)
            (
              Icons.restaurant_menu_rounded,
              'Refeições'.tr,
              MealsView(key: ValueKey('meals-${household.id}'), household: household),
            ),
          if (canPay)
            (
              Icons.receipt_long_rounded,
              'Despesas'.tr,
              ExpensesView(key: ValueKey('expenses-${household.id}'), household: household),
            ),
        ];
        final Widget shopping = tabs.length == 1 ? tabs.single.$3 : _ShoppingTabs(tabs: tabs);
        // Só aparecem as áreas a que o meu papel dá acesso nesta casa (e o Hoje, se houver alguma).
        final visible = [
          for (final s in _sections)
            if (s.module != null &&
                (household.accessTo(s.module!).canView || s.module == HouseholdModule.shopping && (canEat || canPay)))
              s,
        ];
        final sections = [if (visible.isNotEmpty) _sections.first, ...visible];
        final section = sections.where((s) => s.module == _section).firstOrNull ?? sections.firstOrNull;
        // Se a área aberta deixou de se ver, fica-se na que a substituiu (mesmo que a outra volte).
        if (section != null) _section = section.module;
        final index = section == null ? 0 : sections.indexOf(section);
        void select(int i) => setState(() => _section = sections[i].module);
        final Widget content = switch (section?.module) {
          null when section == null => const _NoAreas(),
          null => TodayView(
            key: ValueKey('today-${household.id}'),
            household: household,
            onOpen: (module) => setState(() => _section = module),
          ),
          HouseholdModule.shopping ||
          HouseholdModule.meals ||
          HouseholdModule.expenses ||
          HouseholdModule.home => shopping,
          HouseholdModule.chores => ChoresView(key: ValueKey('chores-${household.id}'), household: household),
          HouseholdModule.calendar => CalendarView(key: ValueKey('calendar-${household.id}'), household: household),
          HouseholdModule.rewards => RewardsView(key: ValueKey('rewards-${household.id}'), household: household),
        };
        // As barras de navegação precisam de pelo menos duas áreas.
        final navigation = sections.length >= 2;

        // Num telemóvel, "Membros e convites" vai para o menu: o nome da casa precisa do espaço (DES-23).
        final narrow = MediaQuery.sizeOf(context).width < 600;
        void openMembers() =>
            Navigator.of(context)
                .push(MaterialPageRoute<void>(builder: (_) => HouseholdScreen(householdId: household.id)));

        return Scaffold(
          appBar: AppBar(
            titleSpacing: narrow ? 8 : null,
            title: _HouseholdSwitcher(session: session, current: household, compact: narrow),
            actions: [
              const NotificationsBell(),
              if (!narrow)
                IconButton(
                  tooltip: 'Membros e convites'.tr,
                  icon: const Icon(Icons.group_rounded),
                  onPressed: openMembers,
                ),
              PopupMenuButton<String>(
                onSelected: (value) => switch (value) {
                  'devices' => pushDeferred<void>(context, devices.loadLibrary, () => devices.DevicesScreen()),
                  'account' => _openAccount(),
                  'home' => pushDeferred<void>(
                    context,
                    homearea.loadLibrary,
                    () => homearea.HomeAreaScreen(household: household),
                  ),
                  'panel' => pushDeferred<void>(
                    context,
                    kitchen.loadLibrary,
                    () => kitchen.KitchenPanelScreen(household: household),
                    fullscreenDialog: true,
                  ),
                  'activity' => pushDeferred<void>(
                    context,
                    activity.loadLibrary,
                    () => activity.ActivityScreen(household: household),
                  ),
                  'help' => pushDeferred<void>(
                    context,
                    help.loadLibrary,
                    () => help.HelpScreen(householdId: household.id),
                  ),
                  'switch-child' => _switchChild(session),
                  'members' => openMembers(),
                  _ => _signOut(session, everywhere: value == 'logout-all'),
                },
                // Com ícones e separadores: o dia a dia em cima, sair em baixo. Uma criança só vê o que é dela.
                itemBuilder: (_) {
                  final child = session.user?.isChildProfile == true;
                  PopupMenuItem<String> item(String value, IconData icon, String label) => PopupMenuItem(
                    value: value,
                    child: Row(
                      children: [
                        Icon(icon),
                        const SizedBox(width: 12),
                        Expanded(child: Text(label)),
                      ],
                    ),
                  );
                  return [
                    PopupMenuItem(enabled: false, child: Text(session.user?.email ?? session.user?.displayName ?? '')),
                    // O painel de cozinha funciona com a sessão de um adulto (conclui tarefas por quem as fez).
                    if (narrow) item('members', Icons.group_rounded, 'Membros e convites'.tr),
                    if (household.role.canManageContent)
                      item('panel', Icons.countertops_rounded, 'Modo painel de cozinha'.tr),
                    if (household.accessTo(HouseholdModule.home).canView)
                      item('home', Icons.cottage_rounded, 'A casa'.tr),
                    if (!child) item('activity', Icons.history_rounded, 'Histórico de alterações'.tr),
                    item('account', Icons.person_rounded, 'A minha conta'.tr),
                    if (!child) item('devices', Icons.devices_rounded, 'Dispositivos com sessão iniciada'.tr),
                    item('help', Icons.help_rounded, 'Ajuda e sugestões'.tr),
                    const PopupMenuDivider(),
                    if (child)
                      item('switch-child', Icons.switch_account_rounded, 'Mudar de criança'.tr)
                    else ...[
                      item('logout', Icons.logout_rounded, 'Terminar sessão'.tr),
                      item('logout-all', Icons.phonelink_erase_rounded, 'Terminar sessão em todos os dispositivos'.tr),
                    ],
                  ];
                },
              ),
            ],
          ),
          body: Column(
            children: [
              _CopyBanner(api: context.read<ApiClient>()),
              Expanded(
                child: wide && navigation
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
                                  selectedIcon: Icon(s.selectedIcon, color: context.kubico.area(s.area).onContainer),
                                  indicatorColor: context.kubico.area(s.area).container,
                                  label: Text(s.module?.label ?? 'Hoje'.tr),
                                ),
                            ],
                          ),
                          const VerticalDivider(width: 1),
                          Expanded(child: content),
                        ],
                      )
                    : content,
              ),
            ],
          ),
          bottomNavigationBar: wide || !navigation
              ? null
              : NavigationBar(
                  selectedIndex: index,
                  onDestinationSelected: select,
                  indicatorColor: context.kubico.area(sections[index].area).container,
                  destinations: [
                    for (final s in sections)
                      NavigationDestination(
                        icon: Icon(s.icon),
                        selectedIcon: Icon(s.selectedIcon, color: context.kubico.area(s.area).onContainer),
                        label: s.module?.label ?? 'Hoje'.tr,
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
  var _resetFeeds = true;

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
  const _HouseholdSwitcher({required this.session, required this.current, this.compact = false});

  final SessionController session;
  final HouseholdSummary current;

  /// No telemóvel, sem o ícone da casa (mais espaço para o nome).
  final bool compact;

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
      // Uma pílula, para se ver que se pode tocar para mudar de casa.
      child: Container(
        padding: const EdgeInsets.fromLTRB(12, 6, 6, 6),
        decoration: BoxDecoration(
          color: Theme.of(context).colorScheme.surfaceContainerHighest,
          borderRadius: BorderRadius.circular(999),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (!compact) ...[const Icon(Icons.cottage_rounded), const SizedBox(width: 8)],
            Flexible(child: Text(current.name, overflow: TextOverflow.ellipsis)),
            const Icon(Icons.arrow_drop_down_rounded),
          ],
        ),
      ),
    );
  }
}

/// As Compras com separadores: as listas, as refeições e as despesas (as que o papel vê).
class _ShoppingTabs extends StatelessWidget {
  const _ShoppingTabs({required this.tabs});

  final List<(IconData, String, Widget)> tabs;

  @override
  Widget build(BuildContext context) => DefaultTabController(
    length: tabs.length,
    child: Column(
      children: [
        TabBar(
          tabs: [for (final (icon, label, _) in tabs) Tab(icon: Icon(icon), text: label)],
        ),
        Expanded(
          child: TabBarView(
            physics: const NeverScrollableScrollPhysics(),
            children: [for (final (_, _, view) in tabs) view],
          ),
        ),
      ],
    ),
  );
}

/// Sem ligação, os ecrãs abrem com a última cópia guardada (F4-14): esta faixa, na cor de aviso, diz que é uma cópia.
class _CopyBanner extends StatelessWidget {
  const _CopyBanner({required this.api});

  final ApiClient api;

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<bool>(
    valueListenable: api.showingCopy,
    builder: (context, copy, _) {
      if (!copy) return const SizedBox.shrink();
      final colors = context.kubico;
      return Semantics(
        liveRegion: true,
        child: Material(
          color: colors.warningContainer,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            child: Row(
              children: [
                Icon(Icons.cloud_off_rounded, color: colors.onWarningContainer),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    'Sem ligação: estás a ver a última cópia guardada. O que fizeres nas tarefas e nas compras envia-se '
                            'quando houver rede.'
                        .tr,
                    style: TextStyle(color: colors.onWarningContainer),
                  ),
                ),
              ],
            ),
          ),
        ),
      );
    },
  );
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
            Icon(Icons.lock_outline_rounded, size: 48, color: theme.colorScheme.outline),
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
          Icon(Icons.shopping_basket_rounded, size: 48, color: theme.colorScheme.outline),
          const SizedBox(height: 12),
          Text('Escolhe uma lista'.tr, style: theme.textTheme.titleMedium),
        ],
      ),
    );
  }
}
