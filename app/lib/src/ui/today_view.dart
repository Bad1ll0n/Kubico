import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'dates_pt.dart';
import 'design/components.dart';
import 'theme.dart';

/// O que a casa tem hoje, num só ecrã (é o ecrã inicial): quem está em casa, os meus pontos, a agenda do dia, as
/// tarefas de hoje e em atraso, as compras por fazer e o que está à espera de aprovação. Cada bloco só aparece a
/// quem tem acesso a essa área. Os blocos levam à área ([onOpen]) para fazer o resto.
class TodayView extends StatefulWidget {
  const TodayView({required this.household, required this.onOpen, super.key});

  final HouseholdSummary household;
  final void Function(HouseholdModule module) onOpen;

  @override
  State<TodayView> createState() => _TodayViewState();
}

class _TodayData {
  _TodayData({
    required this.details,
    required this.absences,
    required this.chores,
    required this.calendar,
    required this.lists,
    required this.rewards,
  });

  final HouseholdDetails details;
  final List<MemberAbsence> absences;
  final List<Chore>? chores;
  final CalendarRange? calendar;
  final List<ShoppingList>? lists;
  final RewardsOverview? rewards;
}

class _TodayViewState extends State<TodayView> {
  _TodayData? _data;
  Object? _error;
  late final _reload = CoalescedReload(_load);
  StreamSubscription<HouseholdEvent>? _events;
  StreamSubscription<void>? _reconnected;

  HouseholdSummary get _household => widget.household;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _events = realtime.events.listen((event) {
      // Tudo o que se mostra aqui pode mudar com estes eventos; as recargas juntam-se (CoalescedReload).
      if (event is ShoppingItemUpserted || event is ShoppingItemDeleted || event is NotificationReceived) return;
      _reload.request();
    });
    _reconnected = realtime.reconnected.listen((_) => _reload.request());
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    _reconnected?.cancel();
    _reload.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    final api = context.read<ApiClient>();
    final id = _household.id;
    final today = DateUtils.dateOnly(clock.now());
    try {
      final results = await Future.wait<Object?>([
        api.getHousehold(id),
        api.getAbsences(id),
        _household.accessTo(HouseholdModule.chores).canView ? api.getChores(id) : Future.value(),
        _household.accessTo(HouseholdModule.calendar).canView ? api.getCalendar(id, today, today) : Future.value(),
        _household.accessTo(HouseholdModule.shopping).canView ? api.getLists(id) : Future.value(),
        _household.accessTo(HouseholdModule.rewards).canView ? api.getRewards(id) : Future.value(),
      ]);
      if (!mounted) return;
      setState(() {
        _data = _TodayData(
          details: results[0]! as HouseholdDetails,
          absences: results[1]! as List<MemberAbsence>,
          chores: results[2] as List<Chore>?,
          calendar: results[3] as CalendarRange?,
          lists: results[4] as List<ShoppingList>?,
          rewards: results[5] as RewardsOverview?,
        );
        _error = null;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final data = _data;
    if (data == null) {
      return _error == null
          ? const Center(child: CircularProgressIndicator())
          : StateView.error(
              message: errorText(_error!),
              offline: _error is ApiException && (_error! as ApiException).statusCode == 0,
              onRetry: _load,
            );
    }

    final me = context.watch<SessionController>().user;
    final today = DateUtils.dateOnly(clock.now());
    final blocks = <Widget>[
      _Greeting(name: data.details.member(me?.id)?.displayName ?? me?.displayName ?? '', today: today),
      _WhoIsHome(details: data.details, absences: data.absences, today: today),
      if (data.rewards != null) ?_myPoints(data, me?.id),
      if (data.calendar != null) _agenda(data, today),
      if (data.chores != null) _chores(data, me?.id, today),
      if (data.lists != null) _shopping(data.lists!),
      if (data.rewards != null && _household.accessTo(HouseholdModule.rewards).canManage) ?_waiting(data),
    ];

    return RefreshIndicator(
      onRefresh: _load,
      child: LayoutBuilder(
        builder: (context, constraints) {
          // Num ecrã largo, duas colunas: a agenda e as tarefas à esquerda, o resto à direita.
          final wide = constraints.maxWidth >= 840;
          final padding = EdgeInsets.symmetric(horizontal: wide ? KubicoSpace.s32 : KubicoSpace.s16, vertical: 8);
          if (!wide) {
            return ListView(padding: padding.copyWith(bottom: 96), children: blocks);
          }
          final [greeting, who, ...rest] = blocks;
          final left = rest.where((b) => b.key == const ValueKey('agenda') || b.key == const ValueKey('chores'));
          final right = rest.where((b) => !left.contains(b));
          return ListView(
            padding: padding.copyWith(bottom: 48),
            children: [
              Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 1280),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      greeting,
                      who,
                      Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Expanded(flex: 3, child: Column(children: [...left])),
                          const SizedBox(width: KubicoSpace.s24),
                          Expanded(flex: 2, child: Column(children: [...right])),
                        ],
                      ),
                    ],
                  ),
                ),
              ),
            ],
          );
        },
      ),
    );
  }

  Widget? _myPoints(_TodayData data, String? me) {
    final mine = data.rewards!.pointsOf(me);
    if (mine == null || data.rewards!.rewards.isEmpty) return null;
    // Os pontos são sobretudo das crianças: um adulto só vê o cartão se também já ganhou pontos.
    if (_household.role != HouseholdRole.child && mine.earned == 0) return null;
    // A recompensa mais barata que ainda não chega (ou a mais cara, se já chegam para todas).
    final rewards = [...data.rewards!.rewards]..sort((a, b) => a.cost.compareTo(b.cost));
    final next = rewards.firstWhere((r) => r.cost > mine.available, orElse: () => rewards.last);
    return _PointsCard(points: mine.available, next: next, onTap: () => widget.onOpen(HouseholdModule.rewards));
  }

  Widget _agenda(_TodayData data, DateTime today) {
    final calendar = data.calendar!;
    final occurrences = [...calendar.on(today)]
      ..sort((a, b) {
        if (a.isAllDay != b.isAllDay) return a.isAllDay ? -1 : 1;
        return (a.startsAt ?? a.startDate!).compareTo(b.startsAt ?? b.startDate!);
      });
    final holiday = calendar.holidayOn(today);
    final colors = context.kubico.area(KubicoArea.calendar);
    return _Block(
      key: const ValueKey('agenda'),
      header: SectionHeader(
        'Agenda de hoje'.tr,
        icon: Icons.calendar_month_rounded,
        color: colors.color,
        count: occurrences.length,
        action: TextButton(onPressed: () => widget.onOpen(HouseholdModule.calendar), child: Text('Ver'.tr)),
      ),
      children: [
        if (holiday != null)
          _Line(
            leading: Icon(Icons.celebration_rounded, color: context.kubico.info),
            title: holiday.name,
            subtitle: 'Feriado'.tr,
          ),
        if (occurrences.isEmpty && holiday == null) _Empty('Nada marcado para hoje.'.tr),
        for (final o in occurrences.take(5))
          if (calendar.events[o.eventId] case final event?)
            _Line(
              leading: SizedBox(
                width: 52,
                child: Text(
                  o.isAllDay ? 'Dia todo'.tr : formatTime(o.startsAt!.toLocal()),
                  style: Theme.of(context).textTheme.labelLarge?.copyWith(fontFeatures: tabularFigures),
                ),
              ),
              accent: event.participantUserIds.isEmpty
                  ? colors.color
                  : memberColor(context, data.details.member(event.participantUserIds.first)?.color ?? ''),
              title: event.title,
              subtitle: event.location,
              trailing: _Avatars([for (final id in event.participantUserIds) ?data.details.member(id)]),
              warning: o.conflictUserIds.isNotEmpty ? 'Há um conflito neste evento.'.tr : null,
            ),
        if (occurrences.length > 5) _Empty('E mais ${occurrences.length - 5}.'.tr),
      ],
    );
  }

  Widget _chores(_TodayData data, String? me, DateTime today) {
    final child = _household.role == HouseholdRole.child;
    final due =
        [
          for (final c in data.chores!)
            if (!c.isDone && c.dueDate != null && !c.dueDate!.isAfter(today) && c.isAvailableOn(today))
              // As crianças veem as suas e as de qualquer pessoa; os adultos veem as da casa toda.
              if (!child || c.assigneeUserId == null || c.assigneeUserId == me) c,
        ]..sort((a, b) {
          final mineA = a.assigneeUserId == me ? 0 : 1;
          final mineB = b.assigneeUserId == me ? 0 : 1;
          return mineA != mineB ? mineA - mineB : a.dueDate!.compareTo(b.dueDate!);
        });
    final overdue = due.where((c) => c.dueDate!.isBefore(today)).length;
    final colors = context.kubico.area(KubicoArea.chores);
    return _Block(
      key: const ValueKey('chores'),
      header: SectionHeader(
        child ? 'As minhas tarefas'.tr : 'Tarefas de hoje'.tr,
        icon: Icons.check_circle_rounded,
        color: colors.color,
        count: due.length,
        action: TextButton(onPressed: () => widget.onOpen(HouseholdModule.chores), child: Text('Ver'.tr)),
      ),
      children: [
        if (overdue > 0)
          _Line(
            leading: Icon(Icons.schedule_rounded, color: context.kubico.warning),
            title: overdue == 1 ? '1 tarefa em atraso'.tr : '$overdue tarefas em atraso'.tr,
          ),
        if (due.isEmpty) _Empty('Tudo feito por hoje!'.tr),
        for (final chore in due.take(6))
          _Line(
            leading: Icon(
              chore.isRoutine
                  ? (chore.timeOfDay == ChoreTimeOfDay.evening ? Icons.bedtime_rounded : Icons.wb_sunny_rounded)
                  : Icons.radio_button_unchecked_rounded,
              color: colors.color,
            ),
            title: chore.title,
            subtitle: chore.isRoutine ? '${chore.doneSteps.length} de ${chore.steps.length} passos'.tr : null,
            trailing: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (chore.points > 0) PointsBadge(chore.points),
                if (data.details.member(chore.assigneeUserId) case final m?) ...[
                  const SizedBox(width: KubicoSpace.s8),
                  MemberAvatar(name: m.displayName, color: m.color, size: 28),
                ],
              ],
            ),
            onTap: () => widget.onOpen(HouseholdModule.chores),
          ),
        if (due.length > 6) _Empty('E mais ${due.length - 6}.'.tr),
      ],
    );
  }

  Widget _shopping(List<ShoppingList> lists) {
    final colors = context.kubico.area(KubicoArea.shopping);
    final pending = lists.where((l) => l.pendingCount > 0).toList();
    return _Block(
      key: const ValueKey('shopping'),
      header: SectionHeader(
        'Compras'.tr,
        icon: Icons.shopping_basket_rounded,
        color: colors.color,
        action: TextButton(onPressed: () => widget.onOpen(HouseholdModule.shopping), child: Text('Ver'.tr)),
      ),
      children: [
        if (pending.isEmpty) _Empty('Não falta comprar nada.'.tr),
        for (final list in pending.take(4))
          _Line(
            leading: Icon(Icons.shopping_cart_rounded, color: colors.color),
            title: list.name,
            subtitle: list.pendingCount == 1 ? '1 por comprar'.tr : '${list.pendingCount} por comprar'.tr,
            trailing: SizedBox(
              width: 56,
              child: LinearProgressIndicator(
                value: list.itemCount == 0 ? 0 : list.checkedCount / list.itemCount,
                color: colors.color,
                minHeight: 6,
                borderRadius: BorderRadius.circular(3),
              ),
            ),
            onTap: () => widget.onOpen(HouseholdModule.shopping),
          ),
      ],
    );
  }

  Widget? _waiting(_TodayData data) {
    final pending = data.rewards!.redemptions.where((r) => r.status == RedemptionStatus.pending).toList();
    if (pending.isEmpty) return null;
    final colors = context.kubico.area(KubicoArea.rewards);
    return _Block(
      key: const ValueKey('waiting'),
      header: SectionHeader(
        'À espera de ti'.tr,
        icon: Icons.star_rounded,
        color: colors.color,
        count: pending.length,
        action: TextButton(onPressed: () => widget.onOpen(HouseholdModule.rewards), child: Text('Ver'.tr)),
      ),
      children: [
        for (final r in pending.take(4))
          _Line(
            leading: switch (data.details.member(r.userId)) {
              final m? => MemberAvatar(name: m.displayName, color: m.color, size: 32),
              null => const Icon(Icons.person_rounded),
            },
            title: r.rewardTitle,
            subtitle: '${data.details.member(r.userId)?.displayName ?? ''} pediu'.tr,
            trailing: PointsBadge(r.points),
            onTap: () => widget.onOpen(HouseholdModule.rewards),
          ),
      ],
    );
  }
}

class _Greeting extends StatelessWidget {
  const _Greeting({required this.name, required this.today});

  final String name;
  final DateTime today;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final hour = clock.now().hour;
    final first = name.trim().split(' ').first;
    final hello = hour < 6 || hour >= 20
        ? 'Boa noite, $first'.tr
        : hour < 13
        ? 'Bom dia, $first'.tr
        : 'Boa tarde, $first'.tr;
    return Padding(
      padding: const EdgeInsets.only(top: KubicoSpace.s16, bottom: KubicoSpace.s8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(hello, style: theme.textTheme.headlineMedium),
          const SizedBox(height: 2),
          Text(
            capitalize(formatLongDate(today)),
            style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
          ),
        ],
      ),
    );
  }
}

/// Os membros da casa, com quem está fora (ausência marcada para hoje) assinalado.
class _WhoIsHome extends StatelessWidget {
  const _WhoIsHome({required this.details, required this.absences, required this.today});

  final HouseholdDetails details;
  final List<MemberAbsence> absences;
  final DateTime today;

  @override
  Widget build(BuildContext context) {
    final away = {
      for (final a in absences)
        if (a.covers(today)) a.userId,
    };
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: KubicoSpace.s8),
      child: Wrap(
        spacing: KubicoSpace.s16,
        runSpacing: KubicoSpace.s12,
        children: [
          for (final m in details.members)
            SizedBox(
              width: 64,
              child: Column(
                children: [
                  MemberAvatar(name: m.displayName, color: m.color, size: 48, away: away.contains(m.userId)),
                  const SizedBox(height: 4),
                  Text(
                    m.displayName.trim().split(' ').first,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.labelMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    textAlign: TextAlign.center,
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

/// Os meus pontos e quanto falta para a próxima recompensa (progresso pessoal, sem comparar com os irmãos).
class _PointsCard extends StatelessWidget {
  const _PointsCard({required this.points, required this.next, required this.onTap});

  final int points;
  final Reward next;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final missing = next.cost - points;
    return Padding(
      padding: const EdgeInsets.only(top: KubicoSpace.s12),
      child: Material(
        color: scheme.tertiaryContainer,
        borderRadius: BorderRadius.circular(KubicoRadius.xl),
        child: InkWell(
          borderRadius: BorderRadius.circular(KubicoRadius.xl),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.all(KubicoSpace.s24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.star_rounded, size: 40, color: scheme.tertiary),
                    const SizedBox(width: KubicoSpace.s8),
                    Text(
                      pointsText(points),
                      style: theme.textTheme.displaySmall?.copyWith(
                        color: scheme.onTertiaryContainer,
                        fontFeatures: tabularFigures,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: KubicoSpace.s12),
                LinearProgressIndicator(
                  value: next.cost == 0 ? 1 : (points / next.cost).clamp(0, 1).toDouble(),
                  minHeight: 10,
                  borderRadius: BorderRadius.circular(5),
                  color: scheme.tertiary,
                  backgroundColor: scheme.surfaceContainerLowest.withValues(alpha: 0.6),
                ),
                const SizedBox(height: KubicoSpace.s8),
                Text(
                  missing > 0 ? 'Faltam $missing para «${next.title}»'.tr : 'Já dá para «${next.title}»!'.tr,
                  style: theme.textTheme.bodyMedium?.copyWith(color: scheme.onTertiaryContainer),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Um bloco do ecrã: um cartão com o cabeçalho da secção e as linhas.
class _Block extends StatelessWidget {
  const _Block({required this.header, required this.children, super.key});

  final Widget header;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: KubicoSpace.s12),
    child: Card(
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(KubicoRadius.lg)),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(KubicoSpace.s16, 0, KubicoSpace.s8, KubicoSpace.s8),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [header, ...children]),
      ),
    ),
  );
}

class _Line extends StatelessWidget {
  const _Line({
    required this.leading,
    required this.title,
    this.subtitle,
    this.trailing,
    this.accent,
    this.warning,
    this.onTap,
  });

  final Widget leading;
  final String title;
  final String? subtitle;
  final Widget? trailing;

  /// Uma barra fina à esquerda (a cor da pessoa no evento).
  final Color? accent;

  /// Um aviso que tem de se ver (não numa tooltip).
  final String? warning;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(KubicoRadius.field),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: KubicoSpace.s8, horizontal: KubicoSpace.s4),
        child: Row(
          children: [
            leading,
            const SizedBox(width: KubicoSpace.s12),
            if (accent != null) ...[
              Container(
                width: 4,
                height: 36,
                decoration: BoxDecoration(color: accent, borderRadius: BorderRadius.circular(2)),
              ),
              const SizedBox(width: KubicoSpace.s12),
            ],
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title, style: theme.textTheme.titleMedium),
                  if (subtitle != null && subtitle!.isNotEmpty)
                    Text(
                      subtitle!,
                      style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    ),
                  if (warning != null)
                    Row(
                      children: [
                        Icon(Icons.error_rounded, size: 16, color: context.kubico.warning),
                        const SizedBox(width: 4),
                        Flexible(
                          child: Text(
                            warning!,
                            style: theme.textTheme.bodySmall?.copyWith(color: context.kubico.onWarningContainer),
                          ),
                        ),
                      ],
                    ),
                ],
              ),
            ),
            if (trailing != null) ...[const SizedBox(width: KubicoSpace.s8), trailing!],
          ],
        ),
      ),
    );
  }
}

class _Avatars extends StatelessWidget {
  const _Avatars(this.members);

  final List<Member> members;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      for (final m in members.take(3))
        Padding(
          padding: const EdgeInsets.only(left: 4),
          child: MemberAvatar(name: m.displayName, color: m.color, size: 28),
        ),
      if (members.length > 3)
        Padding(
          padding: const EdgeInsets.only(left: 4),
          child: Text('+${members.length - 3}', style: Theme.of(context).textTheme.labelMedium),
        ),
    ],
  );
}

class _Empty extends StatelessWidget {
  const _Empty(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: KubicoSpace.s8, horizontal: KubicoSpace.s4),
    child: Text(
      text,
      style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant),
    ),
  );
}
