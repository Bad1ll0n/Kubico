import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import 'calendar_sync_screen.dart';

/// Mostra o endereço pessoal para subscrever o calendário da casa no Google Calendar, no Outlook ou no iPhone.
Future<void> showCalendarFeedDialog(BuildContext context, {required String householdId}) => showDialog<void>(
  context: context,
  builder: (_) => _CalendarFeedDialog(householdId: householdId),
);

class _CalendarFeedDialog extends StatefulWidget {
  const _CalendarFeedDialog({required this.householdId});

  final String householdId;

  @override
  State<_CalendarFeedDialog> createState() => _CalendarFeedDialogState();
}

class _CalendarFeedDialogState extends State<_CalendarFeedDialog> {
  CalendarFeed? _feed;
  Object? _error;
  var _busy = false;

  @override
  void initState() {
    super.initState();
    _load(context.read<ApiClient>().getCalendarFeed(widget.householdId));
  }

  Future<void> _load(Future<CalendarFeed> request) async {
    setState(() => _busy = true);
    try {
      final feed = await request;
      if (mounted) setState(() => _feed = feed);
    } catch (e) {
      if (mounted) setState(() => _error = e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// O endereço antigo deixa de funcionar (por exemplo, se foi partilhado por engano).
  Future<void> _renew() => _load(context.read<ApiClient>().renewCalendarFeed(widget.householdId));

  /// Fecha esta janela e abre o ecrã das contas ligadas.
  void _openSync() {
    final navigator = Navigator.of(context);
    navigator.pop();
    navigator.push(MaterialPageRoute<void>(builder: (_) => CalendarSyncScreen(householdId: widget.householdId)));
  }

  void _copy(String url) {
    Clipboard.setData(ClipboardData(text: url));
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Endereço copiado.'.tr)));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final feed = _feed;
    return AlertDialog(
      title: Text('Ver noutros calendários'.tr),
      scrollable: true,
      content: SizedBox(
        width: 480,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Nos dois sentidos'.tr, style: theme.textTheme.titleSmall),
            const SizedBox(height: 4),
            Text(
              'Liga a tua conta Google ou Microsoft: os eventos da casa aparecem lá e o que mudares lá também muda '
                      'no Kubico.'
                  .tr,
            ),
            const SizedBox(height: 8),
            FilledButton.tonalIcon(
              onPressed: _openSync,
              icon: const Icon(Icons.sync),
              label: Text('Ligar ao Google Calendar ou ao Outlook'.tr),
            ),
            const Divider(height: 32),
            Text('Só para ver'.tr, style: theme.textTheme.titleSmall),
            const SizedBox(height: 4),
            Text(
              'Subscreve este endereço no Google Calendar, no Outlook ou no iPhone para veres os eventos da casa '
                      'junto dos teus. As alterações aparecem lá ao fim de algum tempo (no Google, até umas horas).'
                  .tr,
            ),
            const SizedBox(height: 16),
            if (feed != null)
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: SelectableText(feed.url, style: theme.textTheme.bodySmall),
              )
            else if (_error != null)
              Text(_error.toString(), style: TextStyle(color: theme.colorScheme.error))
            else
              const Center(child: CircularProgressIndicator()),
            const SizedBox(height: 16),
            Text('Como subscrever'.tr, style: theme.textTheme.titleSmall),
            const SizedBox(height: 4),
            Text('Google Calendar (no computador): Outros calendários → + → A partir do URL.'.tr),
            Text('Outlook: Adicionar calendário → Subscrever a partir da Web.'.tr),
            Text('iPhone: Definições → Calendário → Contas → Adicionar conta → Outra → Calendário subscrito.'.tr),
            const SizedBox(height: 12),
            Text(
              'O endereço é só teu: quem o tiver vê os eventos da casa. Se o partilhares por engano, gera um novo.'.tr,
              style: theme.textTheme.bodySmall,
            ),
          ],
        ),
      ),
      actions: [
        TextButton(onPressed: _busy ? null : _renew, child: Text('Gerar novo endereço'.tr)),
        TextButton.icon(
          onPressed: feed == null ? null : () => _copy(feed.url),
          icon: const Icon(Icons.copy),
          label: Text('Copiar'.tr),
        ),
        FilledButton(onPressed: () => Navigator.pop(context), child: Text('Feito'.tr)),
      ],
    );
  }
}
