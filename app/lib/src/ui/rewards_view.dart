import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'allowance_section.dart';
import 'dates_pt.dart';
import 'reward_editor.dart';
import 'theme.dart';

/// Recompensas da casa: o saldo de pontos de cada membro, os pedidos à espera de aprovação, as recompensas
/// que se podem trocar e o histórico. Sincroniza em tempo real (os pontos mudam quando alguém conclui tarefas).
class RewardsView extends StatefulWidget {
  const RewardsView({super.key, required this.household});

  final HouseholdSummary household;

  @override
  State<RewardsView> createState() => _RewardsViewState();
}

class _RewardsViewState extends State<RewardsView> {
  final _subscriptions = <StreamSubscription<Object?>>[];
  RewardsOverview? _overview;
  Object? _error;

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;
  String? get _myUserId => context.read<SessionController>().user?.id;
  ModuleAccess get _access => widget.household.accessTo(HouseholdModule.rewards);
  bool get _canManage => _access.canManage;

  @override
  void initState() {
    super.initState();
    final realtime = context.read<HouseholdRealtime>();
    _subscriptions
      ..add(realtime.events.listen(_onEvent))
      ..add(realtime.reconnected.listen((_) => _load()));
    _load();
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final overview = await _api.getRewards(_householdId);
      if (mounted) {
        setState(() {
          _overview = overview;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  void _onEvent(HouseholdEvent event) {
    switch (event) {
      case RewardsChanged():
        _load();
      // Concluir uma tarefa dá pontos.
      case ChoreUpserted(:final chore) when chore.householdId == _householdId:
        _load();
      default:
        break;
    }
  }

  /// Corre uma ação, mostra a mensagem (ou o erro) e volta a ler tudo, porque os saldos mudam.
  Future<void> _run(Future<String?> Function() action) async {
    try {
      final message = await action();
      if (!mounted) return;
      if (message != null) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(content: Text(message)));
      }
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _redeem(Reward reward) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Trocar ${reward.cost} pontos?'.tr),
        content: Text(
          _canManage ? 'Por "${reward.title}".'.tr : 'Por "${reward.title}". Um adulto tem de aprovar o pedido.'.tr,
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Cancelar'.tr)),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text('Trocar'.tr)),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    await _run(() async {
      final redemption = await _api.redeemReward(_householdId, reward.id);
      return redemption.status == RedemptionStatus.approved
          ? 'Feito! Aproveita.'.tr
          : 'Pedido enviado. Um adulto tem de aprovar.'.tr;
    });
  }

  Future<void> _decide(RewardRedemption redemption, {required bool approve}) => _run(() async {
    await _api.decideRedemption(_householdId, redemption.id, approve: approve);
    return approve ? 'Aprovado.'.tr : 'Recusado. Os pontos voltaram.'.tr;
  });

  Future<void> _cancel(RewardRedemption redemption) => _run(() async {
    await _api.cancelRedemption(_householdId, redemption.id);
    return 'Pedido cancelado. Os pontos voltaram.'.tr;
  });

  Future<void> _edit([Reward? reward]) async {
    final result = await showRewardEditor(context, reward: reward);
    if (result == null || !mounted) return;
    await _run(() async {
      switch (result) {
        case RewardSaved(:final draft) when reward == null:
          await _api.createReward(_householdId, draft);
        case RewardSaved(:final draft):
          await _api.updateReward(_householdId, reward!.id, draft);
        case RewardDeleteRequested():
          await _api.deleteReward(_householdId, reward!.id);
      }
      return null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final overview = _overview;
    if (overview == null) {
      return Center(
        child: _error == null
            ? const CircularProgressIndicator()
            : Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(_error.toString()),
                  TextButton(onPressed: _load, child: Text('Tentar de novo'.tr)),
                ],
              ),
      );
    }

    final me = overview.pointsOf(_myUserId);
    final available = me?.available ?? 0;
    final pending = overview.redemptions.where((r) => r.status == RedemptionStatus.pending).toList();
    final history = overview.redemptions.where((r) => r.status != RedemptionStatus.pending).toList();

    return Scaffold(
      floatingActionButton: _canManage
          ? FloatingActionButton.extended(
              onPressed: _edit,
              icon: const Icon(Icons.add),
              label: Text('Nova recompensa'.tr),
            )
          : null,
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
          children: [
            _PointsCard(points: overview.points, me: me),
            AllowanceSection(household: widget.household, members: overview.points),
            if (pending.isNotEmpty) ...[
              _Header('À espera de aprovação'.tr),
              for (final r in pending)
                _RedemptionTile(
                  redemption: r,
                  member: overview.pointsOf(r.userId),
                  onApprove: _canManage ? () => _decide(r, approve: true) : null,
                  onReject: _canManage ? () => _decide(r, approve: false) : null,
                  onCancel: !_canManage && r.userId == _myUserId ? () => _cancel(r) : null,
                ),
            ],
            _Header('Recompensas'.tr),
            if (overview.rewards.isEmpty)
              Padding(
                padding: const EdgeInsets.all(24),
                child: Text(
                  _canManage
                      ? 'Ainda não há recompensas. Cria a primeira e combina-a com as tarefas!'.tr
                      : 'Ainda não há recompensas.'.tr,
                  textAlign: TextAlign.center,
                ),
              ),
            for (final reward in overview.rewards)
              _RewardTile(
                reward: reward,
                available: available,
                onRedeem: _access.canParticipate ? () => _redeem(reward) : null,
                onTap: _canManage ? () => _edit(reward) : null,
              ),
            if (history.isNotEmpty) ...[
              _Header('Histórico'.tr),
              for (final r in history) _RedemptionTile(redemption: r, member: overview.pointsOf(r.userId)),
            ],
          ],
        ),
      ),
    );
  }
}

class _Header extends StatelessWidget {
  const _Header(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
      child: Text(text, style: Theme.of(context).textTheme.titleSmall),
    );
  }
}

/// O meu saldo em destaque e o dos outros membros por baixo.
class _PointsCard extends StatelessWidget {
  const _PointsCard({required this.points, required this.me});

  final List<MemberPoints> points;
  final MemberPoints? me;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final mine = me;
    final others = points.where((p) => p.userId != mine?.userId).toList();
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Os teus pontos'.tr, style: theme.textTheme.titleSmall),
            const SizedBox(height: 4),
            Text('${mine?.available ?? 0}', style: theme.textTheme.displaySmall),
            if (mine != null && mine.reserved > 0)
              Text('Mais ${mine.reserved} reservados em pedidos à espera.'.tr, style: theme.textTheme.bodySmall),
            if (others.isNotEmpty) ...[
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final p in others)
                    Chip(
                      avatar: CircleAvatar(backgroundColor: colorFromHex(p.color)),
                      label: Text('${p.displayName} · ${p.available} pts'),
                    ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _RewardTile extends StatelessWidget {
  const _RewardTile({required this.reward, required this.available, required this.onRedeem, this.onTap});

  final Reward reward;
  final int available;
  final VoidCallback? onRedeem;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final missing = reward.cost - available;
    final details = [
      if (reward.description != null) reward.description!,
      if (missing > 0) 'Faltam $missing pontos'.tr,
    ].join(' · ');
    return Card(
      child: ListTile(
        onTap: onTap,
        leading: const CircleAvatar(child: Icon(Icons.card_giftcard)),
        title: Text(reward.title),
        subtitle: details.isEmpty ? null : Text(details),
        trailing: FilledButton.tonal(onPressed: missing > 0 ? null : onRedeem, child: Text('${reward.cost} pts')),
      ),
    );
  }
}

/// Um pedido: com aprovar/recusar (adultos) ou cancelar (quem o fez) enquanto está à espera.
class _RedemptionTile extends StatelessWidget {
  const _RedemptionTile({required this.redemption, required this.member, this.onApprove, this.onReject, this.onCancel});

  final RewardRedemption redemption;
  final MemberPoints? member;
  final VoidCallback? onApprove;
  final VoidCallback? onReject;
  final VoidCallback? onCancel;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final name = member?.displayName ?? 'Alguém'.tr;
    final when = formatShortDate(redemption.decidedAt ?? redemption.requestedAt);
    final status = redemption.status == RedemptionStatus.pending ? 'pediu a $when'.tr : redemption.status.label;
    return Card(
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: member == null ? null : colorFromHex(member!.color),
          foregroundColor: Colors.white,
          child: Text(name.isEmpty ? '?' : name[0].toUpperCase()),
        ),
        title: Text(redemption.rewardTitle),
        subtitle: Text('$name · ${redemption.points} pts · $status'),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (onReject != null)
              IconButton(
                tooltip: 'Recusar'.tr,
                onPressed: onReject,
                icon: Icon(Icons.close, color: theme.colorScheme.error),
              ),
            if (onApprove != null)
              IconButton(tooltip: 'Aprovar'.tr, onPressed: onApprove, icon: const Icon(Icons.check)),
            if (onCancel != null) TextButton(onPressed: onCancel, child: Text('Cancelar'.tr)),
          ],
        ),
      ),
    );
  }
}
