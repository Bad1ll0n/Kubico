import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import 'dates_pt.dart';

/// Abre a página de autorização do Google ou da Microsoft no navegador do sistema.
Future<bool> openInBrowser(Uri url) => launchUrl(url, mode: LaunchMode.externalApplication);

/// Ligar o calendário da casa ao Google Calendar ou ao Outlook de quem usa a app, nos dois sentidos.
class CalendarSyncScreen extends StatefulWidget {
  const CalendarSyncScreen({super.key, required this.householdId, this.openUrl = openInBrowser});

  final String householdId;

  /// Como se abre a página de autorização (nos testes, não se abre nada).
  final Future<bool> Function(Uri url) openUrl;

  @override
  State<CalendarSyncScreen> createState() => _CalendarSyncScreenState();
}

class _CalendarSyncScreenState extends State<CalendarSyncScreen> {
  CalendarSyncOverview? _overview;
  Object? _error;

  /// O serviço com um pedido em curso (os botões dele ficam desativados).
  CalendarProvider? _busy;
  StreamSubscription<HouseholdEvent>? _events;
  late final AppLifecycleListener _lifecycle;

  ApiClient get _api => context.read<ApiClient>();

  @override
  void initState() {
    super.initState();
    _events = context.read<HouseholdRealtime>().events.listen((event) {
      if (event is CalendarConnectionsChanged) _load();
    });

    // Ao voltar do navegador, a ligação nova já lá está.
    _lifecycle = AppLifecycleListener(onResume: _load);
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    _lifecycle.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final overview = await _api.getCalendarSync(widget.householdId);
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

  Future<void> _connect(CalendarProvider provider) => _run(provider, () async {
    final url = await _api.authorizeCalendarSync(widget.householdId, provider);
    final opened = await widget.openUrl(url);
    _say(opened ? 'Autoriza o Kubico na página que abriu e depois volta aqui.' : 'Não foi possível abrir o navegador.');
  });

  Future<void> _sync(CalendarConnection connection) => _run(connection.provider, () async {
    final synced = await _api.syncCalendarConnection(widget.householdId, connection.id);
    await _load();
    _say(synced.lastError ?? 'Sincronizado.');
  });

  Future<void> _disconnect(CalendarConnection connection) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Desligar a ${connection.provider.account}?'),
        content: Text(
          'O calendário do Kubico é apagado da conta ${connection.accountEmail}. Os eventos da casa ficam no Kubico.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancelar')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Desligar')),
        ],
      ),
    );
    if (confirmed != true) return;
    await _run(connection.provider, () async {
      await _api.disconnectCalendar(widget.householdId, connection.id);
      await _load();
      _say('${connection.provider.label} desligado.');
    });
  }

  Future<void> _run(CalendarProvider provider, Future<void> Function() action) async {
    setState(() => _busy = provider);
    try {
      await action();
    } on ApiException catch (e) {
      _say(e.message);
    } finally {
      if (mounted) setState(() => _busy = null);
    }
  }

  void _say(String message) {
    if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final overview = _overview;
    return Scaffold(
      appBar: AppBar(title: const Text('Google Calendar e Outlook')),
      body: overview == null
          ? Center(
              child: _error == null
                  ? const CircularProgressIndicator()
                  : Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(_error.toString()),
                        TextButton(onPressed: _load, child: const Text('Tentar de novo')),
                      ],
                    ),
            )
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  const Text(
                    'Liga a tua conta e os eventos da casa aparecem lá num calendário próprio, «Kubico». '
                    'O que mudares nesse calendário também muda no Kubico, ao fim de poucos minutos. '
                    'Os teus outros calendários não são lidos nem alterados.',
                  ),
                  const SizedBox(height: 16),
                  for (final provider in CalendarProvider.values) _card(provider, overview),
                ],
              ),
            ),
    );
  }

  Widget _card(CalendarProvider provider, CalendarSyncOverview overview) {
    final theme = Theme.of(context);
    final small = theme.textTheme.bodySmall;
    final error = small?.copyWith(color: theme.colorScheme.error);
    final connection = overview.connectionFor(provider);
    final busy = _busy == provider;
    final lines = <Widget>[];
    final actions = <Widget>[];

    if (connection != null) {
      lines.add(Text(connection.accountEmail));
      if (connection.needsReconnect) {
        lines.add(Text('O acesso à conta expirou ou foi retirado. Volta a ligar a conta.', style: error));
        if (overview.canConnect) {
          actions.add(
            FilledButton(onPressed: busy ? null : () => _connect(provider), child: const Text('Voltar a ligar')),
          );
        }
      } else {
        lines.add(Text(_syncedText(connection.lastSyncedAt), style: small));
        if (connection.lastError case final problem?) lines.add(Text(problem, style: error));
        actions.add(
          TextButton(onPressed: busy ? null : () => _sync(connection), child: const Text('Sincronizar agora')),
        );
      }
      actions.add(TextButton(onPressed: busy ? null : () => _disconnect(connection), child: const Text('Desligar')));
    } else if (!overview.available.contains(provider)) {
      lines.add(Text('Ainda não está disponível neste servidor.', style: small));
    } else if (!overview.canConnect) {
      lines.add(Text('Só os adultos da casa podem ligar contas.', style: small));
    } else {
      actions.add(
        FilledButton.icon(
          onPressed: busy ? null : () => _connect(provider),
          icon: const Icon(Icons.link),
          label: Text('Ligar a ${provider.account}'),
        ),
      );
    }

    if (provider == CalendarProvider.microsoft && (connection != null || actions.isNotEmpty)) {
      lines.add(Text('No Outlook, as alterações a um só dia de uma série não voltam ao Kubico.', style: small));
    }

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.event_available_outlined),
                const SizedBox(width: 8),
                Expanded(child: Text(provider.label, style: theme.textTheme.titleMedium)),
                if (busy) const SizedBox.square(dimension: 16, child: CircularProgressIndicator(strokeWidth: 2)),
              ],
            ),
            const SizedBox(height: 8),
            for (final line in lines) Padding(padding: const EdgeInsets.only(bottom: 4), child: line),
            if (actions.isNotEmpty) ...[const SizedBox(height: 8), Wrap(spacing: 8, runSpacing: 8, children: actions)],
          ],
        ),
      ),
    );
  }

  static String _syncedText(DateTime? at) {
    if (at == null) return 'À espera da primeira sincronização.';
    return DateUtils.isSameDay(at, DateTime.now())
        ? 'Sincronizado hoje às ${formatTime(at)}.'
        : 'Sincronizado a ${formatDayMonth(at)}, às ${formatTime(at)}.';
  }
}
