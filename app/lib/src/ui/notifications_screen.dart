import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../notifications/notification_center.dart';
import '../notifications/open_notification.dart';
import 'dates_pt.dart';
import 'design/components.dart';
import 'theme.dart';

/// O sino da barra de cima, com quantos avisos estão por ler.
class NotificationsBell extends StatelessWidget {
  const NotificationsBell({super.key});

  @override
  Widget build(BuildContext context) {
    final unread = context.watch<NotificationCenter>().unreadCount;
    return IconButton(
      tooltip: unread == 0 ? 'Avisos'.tr : 'Avisos ($unread por ler)'.tr,
      onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const NotificationsScreen())),
      icon: Badge(
        isLabelVisible: unread > 0,
        label: Text(unread > 99 ? '99+' : '$unread'),
        child: const Icon(Icons.notifications_rounded),
      ),
    );
  }
}

/// Os avisos de todas as casas, do mais recente para o mais antigo.
class NotificationsScreen extends StatefulWidget {
  const NotificationsScreen({super.key});

  @override
  State<NotificationsScreen> createState() => _NotificationsScreenState();
}

class _NotificationsScreenState extends State<NotificationsScreen> {
  @override
  void initState() {
    super.initState();
    context.read<NotificationCenter>().load();
  }

  Future<void> _run(Future<void> Function() action) async {
    try {
      await action();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final center = context.watch<NotificationCenter>();
    return Scaffold(
      appBar: AppBar(
        title: Text('Avisos'.tr),
        actions: [
          if (center.unreadCount > 0)
            TextButton(onPressed: () => _run(center.markAllRead), child: Text('Marcar como lidos'.tr)),
          IconButton(
            tooltip: 'Como receber os avisos'.tr,
            onPressed: () =>
                Navigator.of(context)
                    .push(MaterialPageRoute<void>(builder: (_) => const NotificationPreferencesScreen())),
            icon: const Icon(Icons.tune_rounded),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: center.load,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
          children: [
            if (center.items.isEmpty)
              StateView(
                inList: true,
                icon: Icons.notifications_none_rounded,
                title: 'Sem avisos'.tr,
                message: 'Aparecem aqui quando te atribuem uma tarefa ou um evento.'.tr,
              ),
            for (final n in center.items)
              Card(
                child: ListTile(
                  // Marca como lido e leva à casa e à área do aviso.
                  onTap: () => _run(() async {
                    await center.markRead(n);
                    final target = NotificationTarget.of(n.kind, n.householdId);
                    if (target != null && context.mounted) {
                      Navigator.of(context).popUntil((route) => route.isFirst);
                      OpenNotification.open(target);
                    }
                  }),
                  leading: CircleAvatar(child: Icon(_icon(n.kind))),
                  title: Text(n.title, style: TextStyle(fontWeight: n.isRead ? null : FontWeight.bold)),
                  subtitle: Text(
                    [
                      if (n.body != null) n.body!,
                      '${formatShortDate(n.createdAt)} ${formatTime(n.createdAt)}',
                    ].join(' · '),
                  ),
                  trailing: n.isRead ? null : const Icon(Icons.circle_rounded, size: 10),
                ),
              ),
          ],
        ),
      ),
    );
  }

  static IconData _icon(String kind) => switch (kind) {
    'ChoreAssigned' => Icons.task_alt_rounded,
    'ChoreCompleted' => Icons.photo_camera_rounded,
    'ChoreRejected' => Icons.replay_rounded,
    'AllowancePaid' => Icons.savings_rounded,
    'ChildPinLocked' => Icons.lock_outline_rounded,
    'EventAssigned' => Icons.event_rounded,
    'RideAssigned' => Icons.directions_car_rounded,
    'RewardRequested' || 'RewardDecided' => Icons.card_giftcard_rounded,
    _ => Icons.notifications_rounded,
  };
}

/// Como receber os avisos no telemóvel: logo, num resumo diário ou só na app; e as horas de silêncio.
class NotificationPreferencesScreen extends StatefulWidget {
  const NotificationPreferencesScreen({super.key});

  @override
  State<NotificationPreferencesScreen> createState() => _NotificationPreferencesScreenState();
}

class _NotificationPreferencesScreenState extends State<NotificationPreferencesScreen> {
  NotificationPreferences? _saved;
  var _delivery = NotificationDelivery.immediate;
  var _digestAt = const TimeOfDay(hour: 19, minute: 0);
  var _quiet = false;
  var _quietFrom = const TimeOfDay(hour: 22, minute: 0);
  var _quietUntil = const TimeOfDay(hour: 8, minute: 0);
  var _morning = true;
  var _morningAt = const TimeOfDay(hour: 7, minute: 30);
  var _weekly = true;
  Object? _error;
  var _saving = false;

  ApiClient get _api => context.read<ApiClient>();

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final preferences = await _api.getNotificationPreferences();
      if (!mounted) return;
      setState(() {
        _saved = preferences;
        _delivery = preferences.delivery;
        _digestAt = _toTime(preferences.digestAt);
        _quiet = preferences.quietFrom != null;
        if (preferences.quietFrom case final from?) _quietFrom = _toTime(from);
        if (preferences.quietUntil case final until?) _quietUntil = _toTime(until);
        _morning = preferences.morningSummary;
        _morningAt = _toTime(preferences.morningAt);
        _weekly = preferences.weeklySummary;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _save() async {
    setState(() => _saving = true);
    try {
      await _api.saveNotificationPreferences(
        NotificationPreferences(
          delivery: _delivery,
          digestAt: _fromTime(_digestAt),
          quietFrom: _quiet ? _fromTime(_quietFrom) : null,
          quietUntil: _quiet ? _fromTime(_quietUntil) : null,
          timeZoneId: _saved?.timeZoneId ?? 'Europe/Lisbon',
          morningSummary: _morning,
          morningAt: _fromTime(_morningAt),
          weeklySummary: _weekly,
        ),
      );
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Preferências guardadas.'.tr)));
      Navigator.pop(context);
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _pick(TimeOfDay initial, ValueChanged<TimeOfDay> apply) async {
    final picked = await showTimePicker(context: context, initialTime: initial);
    if (picked != null) setState(() => apply(picked));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final localizations = MaterialLocalizations.of(context);
    String time(TimeOfDay t) => localizations.formatTimeOfDay(t, alwaysUse24HourFormat: true);

    return Scaffold(
      appBar: AppBar(title: Text('Como receber os avisos'.tr)),
      body: _saved == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(errorText(_error!)))
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                Text('No telemóvel'.tr, style: theme.textTheme.titleSmall),
                RadioGroup<NotificationDelivery>(
                  groupValue: _delivery,
                  onChanged: (value) => setState(() => _delivery = value ?? _delivery),
                  child: Column(
                    children: [
                      for (final d in NotificationDelivery.values)
                        RadioListTile<NotificationDelivery>(value: d, title: Text(d.label)),
                    ],
                  ),
                ),
                if (_delivery == NotificationDelivery.dailyDigest)
                  ListTile(
                    leading: const Icon(Icons.schedule_rounded),
                    title: Text('Hora do resumo'.tr),
                    trailing: Text(time(_digestAt)),
                    onTap: () => _pick(_digestAt, (t) => _digestAt = t),
                  ),
                const Divider(height: 32),
                SwitchListTile(
                  title: Text('Horas de silêncio'.tr),
                  subtitle: Text('Os avisos esperam e chegam quando acabarem.'.tr),
                  value: _quiet,
                  onChanged: (value) => setState(() => _quiet = value),
                ),
                if (_quiet) ...[
                  ListTile(
                    leading: const Icon(Icons.bedtime_rounded),
                    title: Text('Das'.tr),
                    trailing: Text(time(_quietFrom)),
                    onTap: () => _pick(_quietFrom, (t) => _quietFrom = t),
                  ),
                  ListTile(
                    leading: const Icon(Icons.wb_sunny_rounded),
                    title: Text('Às'.tr),
                    trailing: Text(time(_quietUntil)),
                    onTap: () => _pick(_quietUntil, (t) => _quietUntil = t),
                  ),
                ],
                const Divider(height: 32),
                // Os resumos (F4-21): o que há hoje, de manhã; e a semana da casa, ao domingo.
                SwitchListTile(
                  title: Text('Resumo da manhã'.tr),
                  subtitle: Text('Os eventos, as tarefas e as compras do dia, numa só mensagem.'.tr),
                  value: _morning,
                  onChanged: (value) => setState(() => _morning = value),
                ),
                if (_morning)
                  ListTile(
                    leading: const Icon(Icons.wb_twilight_rounded),
                    title: Text('Hora do resumo da manhã'.tr),
                    trailing: Text(time(_morningAt)),
                    onTap: () => _pick(_morningAt, (t) => _morningAt = t),
                  ),
                SwitchListTile(
                  title: Text('Resumo da semana'.tr),
                  subtitle: Text(
                    'Ao domingo às 19h, para quem gere as tarefas: o que se fez e o que ficou por fazer.'.tr,
                  ),
                  value: _weekly,
                  onChanged: (value) => setState(() => _weekly = value),
                ),
                const SizedBox(height: 16),
                Text('Todos os avisos aparecem também aqui na app, no sino.'.tr, style: theme.textTheme.bodySmall),
                const SizedBox(height: 24),
                FilledButton(onPressed: _saving ? null : _save, child: Text('Guardar'.tr)),
              ],
            ),
    );
  }

  static TimeOfDay _toTime(({int hour, int minute}) t) => TimeOfDay(hour: t.hour, minute: t.minute);

  static ({int hour, int minute}) _fromTime(TimeOfDay t) => (hour: t.hour, minute: t.minute);
}
