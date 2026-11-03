import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'allowance_section.dart';
import 'dates_pt.dart';
import 'design/components.dart';
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

  /// Os pedidos e os pontos que mudam em rajada (uma criança faz três tarefas seguidas) dão uma só recarga.
  late final _reload = CoalescedReload(_load);

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
    _reload.dispose();
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
        _reload.request();
      // Os pontos mudam com as tarefas feitas (não com qualquer mudança a uma tarefa, como o título).
      case PointsChanged(:final householdId) when householdId == _householdId:
        _reload.request();
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
        title: Text(reward.cost == 1 ? 'Trocar 1 ponto?'.tr : 'Trocar ${reward.cost} pontos?'.tr),
        content: Text(
          _canManage ? 'Por «${reward.title}».'.tr : 'Por «${reward.title}». Um adulto tem de aprovar o pedido.'.tr,
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
      return _error == null
          ? const Center(child: CircularProgressIndicator())
          : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load);
    }

    final me = overview.pointsOf(_myUserId);
    final available = me?.available ?? 0;
    final pending = overview.redemptions.where((r) => r.status == RedemptionStatus.pending).toList();
    final history = overview.redemptions.where((r) => r.status != RedemptionStatus.pending).toList();

    return Scaffold(
      floatingActionButton: _canManage
          ? FloatingActionButton.extended(
              onPressed: _edit,
              icon: const Icon(Icons.add_rounded),
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
              StateView(
                inList: true,
                icon: Icons.card_giftcard_rounded,
                illustration: KubicoIllustration.gift,
                color: context.kubico.area(KubicoArea.rewards).color,
                title: 'Ainda não há recompensas'.tr,
                message: _canManage ? 'Cria a primeira e combina-a com as tarefas!'.tr : null,
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
  Widget build(BuildContext context) =>
      Padding(padding: const EdgeInsets.symmetric(horizontal: 8), child: SectionHeader(text));
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
            Text('Os teus pontos'.tr, style: theme.textTheme.titleMedium),
            const SizedBox(height: 8),
            // O saldo em grande; uma dívida em cor de aviso (não é um erro da criança).
            Row(
              children: [
                Icon(
                  (mine?.available ?? 0) < 0 ? Icons.error_outline_rounded : Icons.star_rounded,
                  size: 40,
                  color: (mine?.available ?? 0) < 0 ? context.kubico.warning : theme.colorScheme.tertiary,
                ),
                const SizedBox(width: 8),
                Text(
                  (mine?.available ?? 0) < 0 ? 'Deves ${-(mine!.available)}'.tr : '${mine?.available ?? 0}',
                  style: theme.textTheme.displaySmall?.copyWith(fontFeatures: tabularFigures),
                ),
              ],
            ),
            if (mine != null && mine.available < 0)
              Text(
                'Trocaste pontos de uma tarefa que depois não foi aceite. Os próximos pontos que ganhares pagam esta dívida.'
                    .tr,
                style: theme.textTheme.bodySmall,
              ),
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
                      avatar: MemberAvatar(name: p.displayName, color: p.color, size: 24),
                      label: Text('${p.displayName} · ${pointsText(p.available)}'),
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
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final missing = reward.cost - available;
    // A barra mostra quanto falta (progresso pessoal). Quando ainda não chega não há botão desativado (para uma
    // criança, um botão cinzento lê-se como "não podes"): só a barra e quanto falta.
    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(KubicoRadius.md),
        child: Padding(
          padding: const EdgeInsets.all(KubicoSpace.s16),
          child: Row(
            children: [
              Container(
                width: 56,
                height: 56,
                decoration: BoxDecoration(
                  color: scheme.tertiaryContainer,
                  borderRadius: BorderRadius.circular(KubicoRadius.md),
                ),
                child: Icon(Icons.card_giftcard_rounded, size: 30, color: scheme.onTertiaryContainer),
              ),
              const SizedBox(width: KubicoSpace.s16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(child: Text(reward.title, style: theme.textTheme.titleMedium)),
                        PointsBadge(reward.cost),
                      ],
                    ),
                    if (reward.description != null)
                      Text(
                        reward.description!,
                        style: theme.textTheme.bodyMedium?.copyWith(color: scheme.onSurfaceVariant),
                      ),
                    const SizedBox(height: KubicoSpace.s8),
                    LinearProgressIndicator(
                      value: reward.cost <= 0 ? 1 : (available / reward.cost).clamp(0, 1).toDouble(),
                      minHeight: 8,
                      borderRadius: BorderRadius.circular(4),
                      color: scheme.tertiary,
                    ),
                    const SizedBox(height: KubicoSpace.s8),
                    if (missing > 0)
                      Text(
                        missing == 1 ? 'Falta 1 ponto'.tr : 'Faltam $missing pontos'.tr,
                        style: theme.textTheme.bodyMedium?.copyWith(color: scheme.onSurfaceVariant),
                      )
                    else if (onRedeem != null)
                      Align(
                        alignment: Alignment.centerLeft,
                        child: FilledButton.icon(
                          style: FilledButton.styleFrom(
                            backgroundColor: scheme.tertiary,
                            foregroundColor: scheme.onTertiary,
                          ),
                          onPressed: onRedeem,
                          icon: const Icon(Icons.redeem_rounded),
                          label: Text('Trocar'.tr),
                        ),
                      ),
                  ],
                ),
              ),
            ],
          ),
        ),
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
        leading: MemberAvatar(name: name, color: member?.color ?? '', size: 40),
        title: Text(redemption.rewardTitle),
        subtitle: Text('$name · ${redemption.points} pts · $status'),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (onReject != null)
              IconButton(
                tooltip: 'Recusar'.tr,
                onPressed: onReject,
                icon: Icon(Icons.close_rounded, color: theme.colorScheme.error),
              ),
            if (onApprove != null)
              IconButton(tooltip: 'Aprovar'.tr, onPressed: onApprove, icon: const Icon(Icons.check_rounded)),
            if (onCancel != null) TextButton(onPressed: onCancel, child: Text('Cancelar'.tr)),
          ],
        ),
      ),
    );
  }
}
