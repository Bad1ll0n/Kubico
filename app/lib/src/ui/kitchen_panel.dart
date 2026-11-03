import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import '../shopping/shopping_repository.dart';
import 'dates_pt.dart';
import 'design/components.dart';
import 'design/screen_awake.dart';
import 'theme.dart';

/// O painel de cozinha: um tablet fixo na parede ou na bancada como centro da casa. Letra para ler a 2–3 m, alvos
/// grandes e só o essencial: a agenda de hoje e de amanhã, as tarefas do dia (concluem-se aqui, escolhendo quem
/// as fez), as compras por fazer (marcam-se aqui) e os pontos de cada criança. Funciona com a sessão de um adulto.
/// "Quem és?": tocar num avatar escolhe quem está a usar o painel durante 2 minutos, e as tarefas concluídas
/// ficam em nome dessa pessoa sem perguntar; sem ninguém escolhido, pergunta-se quem fez.
/// O ecrã fica sempre ligado enquanto o painel está aberto. Das 22h às 7h passa ao modo noite (só o relógio e o
/// primeiro evento de amanhã). Sai-se mantendo o botão
/// carregado, para as crianças não saírem sem querer.
class KitchenPanelScreen extends StatefulWidget {
  const KitchenPanelScreen({required this.household, super.key});

  final HouseholdSummary household;

  @override
  State<KitchenPanelScreen> createState() => _KitchenPanelScreenState();
}

class _PanelData {
  const _PanelData({
    required this.details,
    required this.absences,
    required this.chores,
    required this.calendar,
    required this.list,
    required this.items,
    required this.rewards,
  });

  final HouseholdDetails details;
  final List<MemberAbsence> absences;
  final List<Chore>? chores;
  final CalendarRange? calendar;
  final ShoppingList? list;
  final List<ShoppingItem> items;
  final RewardsOverview? rewards;
}

class _KitchenPanelScreenState extends State<KitchenPanelScreen> {
  _PanelData? _data;
  Object? _error;
  late final _reload = CoalescedReload(_load);
  StreamSubscription<HouseholdEvent>? _events;
  StreamSubscription<void>? _reconnected;
  Timer? _tick;
  DateTime _now = clock.now();

  /// Quem está a usar o painel (escolhido em "Quem és?") e até quando.
  String? _activeUserId;
  DateTime? _activeUntil;

  static const activeFor = Duration(minutes: 2);

  HouseholdSummary get _household => widget.household;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _events = realtime.events.listen((event) {
      if (event is! NotificationReceived) _reload.request();
    });
    _reconnected = realtime.reconnected.listen((_) => _reload.request());
    // O relógio muda ao minuto.
    _tick = Timer.periodic(const Duration(seconds: 20), (_) {
      final now = clock.now();
      if (!mounted) return;
      if (_activeUntil != null && !now.isBefore(_activeUntil!)) {
        setState(() => _activeUserId = _activeUntil = null);
      }
      if (now.minute != _now.minute) setState(() => _now = now);
    });
    ScreenAwake.set(true);
    _load();
  }

  @override
  void dispose() {
    ScreenAwake.set(false);
    _events?.cancel();
    _reconnected?.cancel();
    _tick?.cancel();
    _reload.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    final api = context.read<ApiClient>();
    final shopping = context.read<ShoppingRepository>();
    final id = _household.id;
    final today = DateUtils.dateOnly(clock.now());
    try {
      final results = await Future.wait<Object?>([
        api.getHousehold(id),
        api.getAbsences(id),
        _household.accessTo(HouseholdModule.chores).canView ? api.getChores(id) : Future.value(),
        _household.accessTo(HouseholdModule.calendar).canView
            ? api.getCalendar(id, today, today.add(const Duration(days: 1)))
            : Future.value(),
        _household.accessTo(HouseholdModule.shopping).canView ? shopping.lists(id) : Future.value(),
        _household.accessTo(HouseholdModule.rewards).canView ? api.getRewards(id) : Future.value(),
      ]);
      // A lista com mais coisas por comprar.
      final lists = (results[4] as List<ShoppingList>?) ?? const [];
      final list = lists.isEmpty ? null : (lists.toList()..sort((a, b) => b.pendingCount - a.pendingCount)).first;
      final items = list == null || list.pendingCount == 0 ? <ShoppingItem>[] : await shopping.items(id, list.id);
      if (!mounted) return;
      setState(() {
        _data = _PanelData(
          details: results[0]! as HouseholdDetails,
          absences: results[1]! as List<MemberAbsence>,
          chores: results[2] as List<Chore>?,
          calendar: results[3] as CalendarRange?,
          list: list,
          items: items.where((i) => !i.isChecked).toList(),
          rewards: results[5] as RewardsOverview?,
        );
        _error = null;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  bool get _night => _now.hour >= 22 || _now.hour < 7;

  /// Quem está a usar o painel agora (null se ninguém escolheu ou já passaram os 2 minutos).
  String? get _active =>
      _activeUserId != null && _activeUntil != null && clock.now().isBefore(_activeUntil!) ? _activeUserId : null;

  void _choose(Member member) => setState(() {
    if (_active == member.userId) {
      _activeUserId = _activeUntil = null;
    } else {
      _activeUserId = member.userId;
      _activeUntil = clock.now().add(activeFor);
    }
  });

  Future<void> _complete(Chore chore) async {
    final data = _data!;
    // No painel quem usa é a casa toda: vai em nome de quem escolheu "Quem és?" ou pergunta-se quem fez (os pontos
    // vão para essa pessoa).
    final doneBy =
        _active ??
        await showDialog<String>(
          context: context,
          builder: (context) => _WhoDidIt(chore: chore, members: data.details.members),
        );
    if (doneBy == null || !mounted) return;
    try {
      final (_, points) = await context.read<ApiClient>().completeChore(_household.id, chore.id, doneByUserId: doneBy);
      if (!mounted) return;
      celebratePoints(context, points);
      // Quem está a usar o painel continua mais 2 minutos.
      if (_active == doneBy) _activeUntil = clock.now().add(activeFor);
      _reload.request();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  void _check(ShoppingItem item) {
    context.read<ShoppingRepository>().setChecked(_household.id, item.listId, item.id, true);
    setState(() => _data!.items.remove(item));
  }

  @override
  Widget build(BuildContext context) {
    final media = MediaQuery.of(context);
    // Letra 35 % maior (por cima da do sistema): lê-se do outro lado da cozinha.
    final scaled = (media.textScaler.scale(16) / 16 * 1.35).clamp(1.0, 2.0);
    final dark = _night;
    final theme = buildTheme(dark ? Brightness.dark : Brightness.light);
    return Theme(
      data: dark ? theme.copyWith(scaffoldBackgroundColor: const Color(0xFF0F0B09)) : theme,
      child: MediaQuery(
        data: media.copyWith(textScaler: TextScaler.linear(scaled)),
        child: Builder(
          builder: (context) => Scaffold(
            body: SafeArea(
              child: switch ((_data, _error)) {
                (null, null) => const Center(child: CircularProgressIndicator()),
                (null, final error?) => StateView.error(
                  message: errorText(error),
                  offline: isOffline(error),
                  onRetry: _load,
                ),
                (final data?, _) => dark ? _NightView(now: _now, data: data) : _dayView(context, data),
              },
            ),
          ),
        ),
      ),
    );
  }

  Widget _dayView(BuildContext context, _PanelData data) {
    final theme = Theme.of(context);
    final today = data.details.todayAt(_now);
    final tomorrow = DateTime(today.year, today.month, today.day + 1);
    final wide = MediaQuery.sizeOf(context).width >= 900;
    final tiles = [
      if (data.calendar != null)
        _Tile(
          area: KubicoArea.calendar,
          icon: Icons.calendar_month_rounded,
          title: 'Agenda'.tr,
          child: _Agenda(calendar: data.calendar!, details: data.details, today: today, tomorrow: tomorrow),
        ),
      if (data.chores != null)
        _Tile(
          area: KubicoArea.chores,
          icon: Icons.check_circle_rounded,
          title: 'Tarefas de hoje'.tr,
          child: _Chores(
            chores: [
              for (final c in data.chores!)
                if (!c.isDone && c.dueDate != null && !c.dueDate!.isAfter(today) && c.isAvailableOn(today)) c,
            ],
            details: data.details,
            onComplete: _household.accessTo(HouseholdModule.chores).canParticipate ? _complete : null,
          ),
        ),
      if (data.list != null)
        _Tile(
          area: KubicoArea.shopping,
          icon: Icons.shopping_basket_rounded,
          title: data.list!.name,
          child: _Shopping(
            items: data.items,
            onCheck: _household.accessTo(HouseholdModule.shopping).canParticipate ? _check : null,
          ),
        ),
    ];
    final away = {
      for (final a in data.absences)
        if (a.covers(today)) a.userId,
    };

    return Padding(
      padding: EdgeInsets.all(wide ? 32 : 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Cabeçalho: a casa e a data, o relógio, quem está em casa e sair.
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(data.details.name, style: theme.textTheme.titleLarge),
                    Text(
                      capitalize(formatLongDate(today)),
                      style: theme.textTheme.titleMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    ),
                  ],
                ),
              ),
              Text(
                formatTime(_now),
                textScaler: TextScaler.noScaling,
                style: theme.textTheme.displayLarge?.copyWith(fontFeatures: tabularFigures),
              ),
              const SizedBox(width: 24),
              _ExitButton(onExit: () => Navigator.of(context).pop()),
            ],
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 12,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text('Quem és?'.tr, style: theme.textTheme.titleMedium),
              for (final m in data.details.members)
                _WhoAmI(
                  member: m,
                  away: away.contains(m.userId),
                  active: _active == m.userId,
                  onTap: away.contains(m.userId) ? null : () => _choose(m),
                ),
            ],
          ),
          const SizedBox(height: 16),
          Expanded(
            child: wide
                ? Row(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      for (final (i, tile) in tiles.indexed) ...[
                        if (i > 0) const SizedBox(width: 24),
                        Expanded(child: tile),
                      ],
                    ],
                  )
                : ListView(
                    children: [
                      for (final tile in tiles)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 16),
                          child: SizedBox(height: 360, child: tile),
                        ),
                    ],
                  ),
          ),
          if (data.rewards != null) _PointsFooter(rewards: data.rewards!, details: data.details),
        ],
      ),
    );
  }
}

/// Um mosaico do painel: cartão branco (no claro), cantos de 32, o ícone e o nome da área na cor dela.
class _Tile extends StatelessWidget {
  const _Tile({required this.area, required this.icon, required this.title, required this.child});

  final KubicoArea area;
  final IconData icon;
  final String title;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = context.kubico.area(area);
    return Material(
      color: theme.colorScheme.surfaceContainerLowest,
      borderRadius: BorderRadius.circular(KubicoRadius.xl),
      elevation: theme.brightness == Brightness.light ? 1 : 0,
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Icon(icon, size: 28, color: colors.color),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.titleLarge?.copyWith(color: colors.color),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Expanded(child: child),
          ],
        ),
      ),
    );
  }
}

class _Agenda extends StatelessWidget {
  const _Agenda({required this.calendar, required this.details, required this.today, required this.tomorrow});

  final CalendarRange calendar;
  final HouseholdDetails details;
  final DateTime today;
  final DateTime tomorrow;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    List<Widget> day(String label, DateTime date) {
      final occurrences = [...calendar.on(date)]
        ..sort((a, b) => (a.startsAt ?? a.startDate!).compareTo(b.startsAt ?? b.startDate!));
      return [
        Padding(
          padding: const EdgeInsets.only(top: 8, bottom: 4),
          child: Text(label.toUpperCase(), style: theme.textTheme.labelLarge?.copyWith(letterSpacing: 1)),
        ),
        if (occurrences.isEmpty)
          Text(
            'Nada marcado.'.tr,
            style: theme.textTheme.bodyLarge?.copyWith(color: theme.colorScheme.onSurfaceVariant),
          ),
        for (final o in occurrences)
          if (calendar.events[o.eventId] case final event?)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Container(
                    width: 4,
                    height: 40,
                    decoration: BoxDecoration(
                      color: event.participantUserIds.isEmpty
                          ? context.kubico.area(KubicoArea.calendar).color
                          : memberColor(context, details.member(event.participantUserIds.first)?.color ?? ''),
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Text(
                    o.isAllDay ? 'Dia todo'.tr : formatTime(o.startsAt!.toLocal()),
                    style: theme.textTheme.titleMedium?.copyWith(fontFeatures: tabularFigures),
                  ),
                  const SizedBox(width: 12),
                  Expanded(child: Text(event.title, style: theme.textTheme.titleMedium)),
                ],
              ),
            ),
      ];
    }

    return ListView(children: [...day('Hoje'.tr, today), ...day('Amanhã'.tr, tomorrow)]);
  }
}

class _Chores extends StatelessWidget {
  const _Chores({required this.chores, required this.details, required this.onComplete});

  final List<Chore> chores;
  final HouseholdDetails details;
  final void Function(Chore chore)? onComplete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (chores.isEmpty) {
      return Center(child: Text('Tudo feito por hoje!'.tr, style: theme.textTheme.titleMedium));
    }
    return ListView(
      children: [
        for (final chore in chores)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Row(
              children: [
                // As rotinas e as que pedem foto fazem-se na app (passos, câmara); aqui só as simples.
                if (onComplete != null && !chore.isRoutine && !chore.requiresPhoto)
                  IconButton(
                    iconSize: 40,
                    constraints: const BoxConstraints(minWidth: 64, minHeight: 64),
                    tooltip: 'Marcar «${chore.title}» como feita'.tr,
                    icon: Icon(
                      Icons.radio_button_unchecked_rounded,
                      color: context.kubico.area(KubicoArea.chores).color,
                    ),
                    onPressed: () => onComplete!(chore),
                  )
                else
                  // Rotinas (passos) e tarefas com foto fazem-se na app: aqui mostra-se de que tipo são.
                  SizedBox(
                    width: 64,
                    child: Icon(
                      chore.requiresPhoto
                          ? Icons.photo_camera_rounded
                          : chore.timeOfDay == ChoreTimeOfDay.evening
                          ? Icons.bedtime_rounded
                          : Icons.wb_sunny_rounded,
                      size: 32,
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                Expanded(child: Text(chore.title, style: theme.textTheme.titleMedium)),
                if (chore.points > 0) PointsBadge(chore.points),
                if (details.member(chore.assigneeUserId) case final m?) ...[
                  const SizedBox(width: 8),
                  MemberAvatar(name: m.displayName, color: m.color, size: 36),
                ],
              ],
            ),
          ),
      ],
    );
  }
}

class _Shopping extends StatelessWidget {
  const _Shopping({required this.items, required this.onCheck});

  final List<ShoppingItem> items;
  final void Function(ShoppingItem item)? onCheck;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (items.isEmpty) {
      return Center(child: Text('Não falta comprar nada.'.tr, style: theme.textTheme.titleMedium));
    }
    return ListView(
      children: [
        for (final item in items)
          InkWell(
            onTap: onCheck == null ? null : () => onCheck!(item),
            borderRadius: BorderRadius.circular(KubicoRadius.field),
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 64),
              child: Row(
                children: [
                  Icon(
                    Icons.radio_button_unchecked_rounded,
                    size: 32,
                    color: context.kubico.area(KubicoArea.shopping).color,
                  ),
                  const SizedBox(width: 12),
                  Expanded(child: Text(item.name, style: theme.textTheme.titleMedium)),
                  if (item.quantity != null)
                    Text(item.quantity!, style: theme.textTheme.titleMedium?.copyWith(fontFeatures: tabularFigures)),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

/// Os pontos de cada criança e quanto falta para a próxima recompensa (cada uma com a sua barra; sem ranking).
class _PointsFooter extends StatelessWidget {
  const _PointsFooter({required this.rewards, required this.details});

  final RewardsOverview rewards;
  final HouseholdDetails details;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final children = [
      for (final m in details.members)
        if (m.role == HouseholdRole.child)
          if (rewards.pointsOf(m.userId) case final points?) (m, points),
    ];
    if (children.isEmpty || rewards.rewards.isEmpty) return const SizedBox.shrink();
    final sorted = [...rewards.rewards]..sort((a, b) => a.cost.compareTo(b.cost));
    return Padding(
      padding: const EdgeInsets.only(top: 16),
      child: Wrap(
        spacing: 32,
        runSpacing: 12,
        children: [
          for (final (member, points) in children)
            SizedBox(
              width: 320,
              child: Row(
                children: [
                  MemberAvatar(name: member.displayName, color: member.color, size: 44),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            Icon(Icons.star_rounded, color: theme.colorScheme.tertiary),
                            const SizedBox(width: 4),
                            Text(pointsText(points.available), style: theme.textTheme.titleMedium),
                          ],
                        ),
                        if (sorted.firstWhere((r) => r.cost > points.available, orElse: () => sorted.last)
                            case final next) ...[
                          LinearProgressIndicator(
                            value: next.cost <= 0 ? 1 : (points.available / next.cost).clamp(0, 1).toDouble(),
                            minHeight: 8,
                            borderRadius: BorderRadius.circular(4),
                            color: theme.colorScheme.tertiary,
                          ),
                          Text(
                            next.title,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                          ),
                        ],
                      ],
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

/// À noite: só o relógio e o primeiro evento de amanhã, em letra clara sobre fundo quase preto.
class _NightView extends StatelessWidget {
  const _NightView({required this.now, required this.data});

  final DateTime now;
  final _PanelData data;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final tomorrow = DateTime(now.year, now.month, now.day + (now.hour >= 22 ? 1 : 0));
    final first = ([
      ...?data.calendar?.on(tomorrow),
    ]..sort((a, b) => (a.startsAt ?? a.startDate!).compareTo(b.startsAt ?? b.startDate!))).firstOrNull;
    final event = first == null ? null : data.calendar!.events[first.eventId];
    return Stack(
      children: [
        Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                formatTime(now),
                textScaler: TextScaler.noScaling,
                style: theme.textTheme.displayLarge?.copyWith(fontSize: 120, fontFeatures: tabularFigures),
              ),
              if (event != null)
                Text(
                  '${first!.isAllDay ? 'Dia todo'.tr : formatTime(first.startsAt!.toLocal())} · ${event.title}',
                  style: theme.textTheme.titleLarge?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                ),
            ],
          ),
        ),
        Positioned(top: 16, right: 16, child: _ExitButton(onExit: () => Navigator.of(context).pop())),
      ],
    );
  }
}

/// Sair do painel: é preciso manter carregado (um toque de uma criança não sai).
class _ExitButton extends StatelessWidget {
  const _ExitButton({required this.onExit});

  final VoidCallback onExit;

  @override
  Widget build(BuildContext context) => Tooltip(
    message: 'Mantém carregado para sair do painel'.tr,
    child: Semantics(
      button: true,
      label: 'Sair do painel'.tr,
      onLongPress: onExit,
      excludeSemantics: true,
      child: GestureDetector(
        onLongPress: onExit,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Icon(Icons.close_fullscreen_rounded, size: 32, color: Theme.of(context).colorScheme.onSurfaceVariant),
        ),
      ),
    ),
  );
}

/// Um avatar de "Quem és?": tocar escolhe (ou larga) quem está a usar o painel; a pessoa escolhida tem um anel e
/// o nome ao lado (a cor nunca vai sozinha).
class _WhoAmI extends StatelessWidget {
  const _WhoAmI({required this.member, required this.away, required this.active, required this.onTap});

  final Member member;
  final bool away;
  final bool active;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Semantics(
      button: onTap != null,
      selected: active,
      label: active ? '${member.displayName} está a usar o painel'.tr : 'Sou ${member.displayName}'.tr,
      excludeSemantics: true,
      child: InkWell(
        key: ValueKey('who-${member.userId}'),
        onTap: onTap,
        customBorder: const StadiumBorder(),
        child: Container(
          constraints: const BoxConstraints(minHeight: 64, minWidth: 64),
          padding: const EdgeInsets.all(4),
          decoration: active
              ? ShapeDecoration(
                  shape: StadiumBorder(side: BorderSide(color: theme.colorScheme.primary, width: 3)),
                  color: theme.colorScheme.primaryContainer,
                )
              : null,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              MemberAvatar(name: member.displayName, color: member.color, size: 52, away: away),
              if (active) ...[
                const SizedBox(width: 8),
                Text(
                  member.displayName,
                  style: theme.textTheme.titleMedium?.copyWith(color: theme.colorScheme.onPrimaryContainer),
                ),
                const SizedBox(width: 12),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// "Quem fez?" com avatares grandes.
class _WhoDidIt extends StatelessWidget {
  const _WhoDidIt({required this.chore, required this.members});

  final Chore chore;
  final List<Member> members;

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text('Quem fez «${chore.title}»?'.tr),
    content: Wrap(
      spacing: 16,
      runSpacing: 16,
      children: [
        for (final m in members)
          InkWell(
            borderRadius: BorderRadius.circular(KubicoRadius.lg),
            onTap: () => Navigator.pop(context, m.userId),
            child: Padding(
              padding: const EdgeInsets.all(8),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  MemberAvatar(name: m.displayName, color: m.color, size: 72),
                  const SizedBox(height: 4),
                  Text(m.displayName, style: Theme.of(context).textTheme.titleMedium),
                ],
              ),
            ),
          ),
      ],
    ),
    actions: [TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr))],
  );
}
