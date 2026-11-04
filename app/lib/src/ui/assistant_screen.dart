import 'dart:typed_data';

import 'package:clock/clock.dart';
import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../session/session_controller.dart';
import '../shopping/shopping_repository.dart';
import 'dates_pt.dart';
import 'design/components.dart';
import 'theme.dart';

/// Um ficheiro para o assistente: a fotografia de um talão ou um PDF (o calendário da escola).
typedef AssistantFile = ({Uint8List bytes, String name, String mediaType});

/// Escolhe o ficheiro a enviar (substituível nos testes).
typedef AssistantFilePicker = Future<AssistantFile?> Function(BuildContext context, {required bool pdf});

/// O assistente (F4-24): escreve-se uma frase ("põe leite e pão e marca o dentista sexta às 18h"), ou junta-se a
/// fotografia de um talão ou o PDF do calendário da escola, e o Claude propõe ações. Nada acontece sem a pessoa
/// escolher: cada ação passa pelos pedidos normais da app, com as permissões de quem a faz.
class AssistantScreen extends StatefulWidget {
  const AssistantScreen({required this.household, this.pickFile = pickAssistantFile, super.key});

  final HouseholdSummary household;
  final AssistantFilePicker pickFile;

  @override
  State<AssistantScreen> createState() => _AssistantScreenState();
}

class _AssistantScreenState extends State<AssistantScreen> {
  final _text = TextEditingController();
  late final ApiClient _api = context.read<ApiClient>();
  AssistantStatus? _status;
  Object? _loadError;
  bool _asking = false;
  bool _running = false;
  String? _error;
  AssistantFile? _file;
  AssistantProposal? _proposal;
  final _chosen = <int>{};
  final _done = <int>{};
  final _failed = <int, String>{};

  String get _householdId => widget.household.id;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() => _loadError = null);
    try {
      final status = await _api.getAssistant(_householdId);
      if (mounted) setState(() => _status = status);
    } catch (e) {
      if (mounted) setState(() => _loadError = e);
    }
  }

  Future<void> _attach({required bool pdf}) async {
    final file = await widget.pickFile(context, pdf: pdf);
    if (file != null && mounted) setState(() => _file = file);
  }

  Future<void> _ask() async {
    if (_text.text.trim().isEmpty && _file == null) {
      setState(() => _error = 'Escreve o que queres ou junta uma fotografia ou um PDF.'.tr);
      return;
    }
    setState(() {
      _asking = true;
      _error = null;
    });
    try {
      final proposal = await _api.askAssistant(
        _householdId,
        text: _text.text,
        file: _file?.bytes,
        mediaType: _file?.mediaType,
      );
      if (!mounted) return;
      setState(() {
        _proposal = proposal;
        _chosen
          ..clear()
          ..addAll(List.generate(proposal.actions.length, (i) => i));
        _done.clear();
        _failed.clear();
        final status = _status;
        if (status != null) {
          _status = AssistantStatus(
            available: status.available,
            enabled: status.enabled,
            usedToday: proposal.usedToday,
            dailyLimit: proposal.dailyLimit,
          );
        }
      });
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    } finally {
      if (mounted) setState(() => _asking = false);
    }
  }

  /// Faz as ações escolhidas, uma a uma; as que falham ficam marcadas com o erro.
  Future<void> _run() async {
    final proposal = _proposal;
    if (proposal == null) return;
    setState(() => _running = true);
    final shopping = context.read<ShoppingRepository>();
    final me = context.read<SessionController>().user?.id;
    List<String>? adults;
    for (final i in _chosen.toList()..sort()) {
      if (_done.contains(i)) continue;
      final action = proposal.actions[i];
      try {
        switch (action.kind) {
          case AssistantActionKind.addItems:
            for (final name in action.items) {
              shopping.addItem(_householdId, action.listId!, name: name);
            }
          case AssistantActionKind.checkItems:
            for (final id in action.itemIds) {
              shopping.setChecked(_householdId, action.listId!, id, true);
            }
          case AssistantActionKind.createEvent:
            final timed = action.startsAt != null;
            await _api.createEvent(
              _householdId,
              CalendarEventDraft(
                title: action.title!,
                start: timed ? action.startsAt! : action.date!,
                end: timed ? action.endsAt! : (action.endDate ?? action.date!),
                isAllDay: !timed,
                location: action.location,
              ),
            );
          case AssistantActionKind.createChore:
            await _api.createChore(_householdId, ChoreDraft(title: action.title!, dueDate: action.date));
          case AssistantActionKind.createExpense:
            // Paga quem pediu, dividido pelos adultos da casa (as crianças não entram nas despesas).
            adults ??= [
              for (final m in (await _api.getHousehold(_householdId)).members)
                if (!m.isChildProfile) m.userId,
            ];
            await _api.saveExpense(
              _householdId,
              ExpenseDraft(
                description: action.title!,
                amountCents: action.amountCents!,
                paidByUserId: me!,
                date: action.date ?? DateUtils.dateOnly(clock.now()),
                splitWith: adults,
              ),
            );
        }
        if (mounted) setState(() => _done.add(i));
      } catch (e) {
        if (mounted) setState(() => _failed[i] = errorText(e));
      }
    }
    if (!mounted) return;
    setState(() => _running = false);
    if (_failed.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Feito.'.tr)));
    }
  }

  Future<void> _toggleEnabled(bool enabled) async {
    try {
      await _api.saveAssistantSettings(_householdId, enabled: enabled);
      await _load();
    } catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(errorText(e))));
    }
  }

  @override
  Widget build(BuildContext context) {
    final status = _status;
    final admin = widget.household.role.canManageMembers;
    return Scaffold(
      appBar: AppBar(title: Text('Assistente'.tr)),
      body: switch ((status, _loadError)) {
        (_, final Object error) => StateView.error(message: errorText(error), onRetry: _load),
        (null, _) => const Center(child: CircularProgressIndicator()),
        (final AssistantStatus s, _) when !s.available => StateView(
          icon: Icons.auto_awesome_rounded,
          title: 'O assistente não está disponível'.tr,
          message: 'Este servidor ainda não tem o assistente configurado.'.tr,
        ),
        (final AssistantStatus s, _) when !s.enabled => StateView(
          icon: Icons.auto_awesome_rounded,
          title: 'O assistente está desligado nesta casa'.tr,
          message: admin ? null : 'Quem administra a casa pode ligá-lo.'.tr,
          action: admin
              ? FilledButton(onPressed: () => _toggleEnabled(true), child: Text('Ligar o assistente'.tr))
              : null,
        ),
        (final AssistantStatus s, _) => _content(context, s, admin),
      },
    );
  }

  Widget _content(BuildContext context, AssistantStatus status, bool admin) {
    final theme = Theme.of(context);
    final proposal = _proposal;
    final file = _file;
    return ListView(
      padding: readablePadding(context, top: 16, bottom: 32),
      children: [
        Text(
          'Escreve o que precisas, ou junta a fotografia de um talão ou o PDF do calendário da escola. '
                  'Eu proponho e tu escolhes o que fazer.'
              .tr,
          style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _text,
          minLines: 2,
          maxLines: 5,
          maxLength: 1000,
          textInputAction: TextInputAction.newline,
          decoration: InputDecoration(
            labelText: 'O que queres fazer?'.tr,
            hintText: 'Põe leite e pão na lista e marca o dentista sexta às 18h'.tr,
            errorText: _error,
          ),
        ),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            OutlinedButton.icon(
              onPressed: _asking ? null : () => _attach(pdf: false),
              icon: const Icon(Icons.receipt_long_rounded),
              label: Text('Fotografia de um talão'.tr),
            ),
            OutlinedButton.icon(
              onPressed: _asking ? null : () => _attach(pdf: true),
              icon: const Icon(Icons.picture_as_pdf_rounded),
              label: Text('PDF'.tr),
            ),
          ],
        ),
        if (file != null)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: InputChip(
              avatar: Icon(file.mediaType == 'application/pdf' ? Icons.picture_as_pdf_rounded : Icons.image_rounded),
              label: Text(file.name),
              onDeleted: () => setState(() => _file = null),
              deleteButtonTooltipMessage: 'Tirar o ficheiro'.tr,
            ),
          ),
        const SizedBox(height: 16),
        FilledButton.icon(
          onPressed: _asking || status.usedToday >= status.dailyLimit ? null : _ask,
          icon: _asking
              ? const SizedBox.square(dimension: 18, child: CircularProgressIndicator(strokeWidth: 2))
              : const Icon(Icons.auto_awesome_rounded),
          label: Text(_asking ? 'A pensar…'.tr : 'Pedir propostas'.tr),
        ),
        const SizedBox(height: 4),
        Text(_usage(status), style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
        if (proposal != null) ...[
          const SizedBox(height: 24),
          if (proposal.reply != null) Text(proposal.reply!, style: theme.textTheme.titleMedium),
          if (proposal.skipped > 0)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text(
                _skipped(proposal.skipped),
                style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurfaceVariant),
              ),
            ),
          const SizedBox(height: 8),
          for (var i = 0; i < proposal.actions.length; i++) _actionTile(context, i, proposal.actions[i]),
          if (proposal.actions.isNotEmpty) ...[
            const SizedBox(height: 12),
            FilledButton(
              onPressed: _running || _chosen.difference(_done).isEmpty ? null : _run,
              child: Text(_running ? 'A fazer…'.tr : 'Fazer as escolhidas'.tr),
            ),
          ],
        ],
        if (admin) ...[
          const SizedBox(height: 32),
          const Divider(),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text('Assistente ligado nesta casa'.tr),
            subtitle: Text('Os pedidos vão para o Claude, da Anthropic. Nunca levam dados das crianças.'.tr),
            value: status.enabled,
            onChanged: _toggleEnabled,
          ),
        ],
      ],
    );
  }

  String _usage(AssistantStatus status) {
    final used = status.usedToday;
    final limit = status.dailyLimit;
    return '$used de $limit pedidos hoje nesta casa'.tr;
  }

  String _skipped(int count) => '$count propostas ficaram de fora (sem permissão ou sem dados suficientes).'.tr;

  Widget _actionTile(BuildContext context, int index, AssistantAction action) {
    final done = _done.contains(index);
    final failed = _failed[index];
    final (icon, title, subtitle) = describeAssistantAction(action);
    return Card(
      margin: const EdgeInsets.symmetric(vertical: 4),
      child: CheckboxListTile(
        value: done || _chosen.contains(index),
        onChanged: done || _running
            ? null
            : (on) => setState(() => on == true ? _chosen.add(index) : _chosen.remove(index)),
        secondary: Icon(done ? Icons.check_circle_rounded : icon),
        title: Text(title),
        subtitle: Text(
          failed ?? (done ? 'Feito.'.tr : subtitle),
          style: failed == null ? null : TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      ),
    );
  }
}

/// O ícone, o título e o pormenor de uma proposta, como aparecem na lista.
(IconData, String, String) describeAssistantAction(AssistantAction a) {
  final list = a.listName ?? '';
  final title = a.title ?? '';
  switch (a.kind) {
    case AssistantActionKind.addItems:
      return (Icons.add_shopping_cart_rounded, 'Pôr na lista «$list»'.tr, a.items.join(', '));
    case AssistantActionKind.checkItems:
      return (Icons.shopping_cart_checkout_rounded, 'Marcar como comprado em «$list»'.tr, a.items.join(', '));
    case AssistantActionKind.createEvent:
      final starts = a.startsAt;
      final date = a.date;
      final end = a.endDate;
      final when = starts != null
          ? '${formatLongDate(starts)}, ${formatTime(starts)}–${formatTime(a.endsAt ?? starts)}'
          : date != null && end != null && end != date
          ? '${formatShortDate(date)} – ${formatShortDate(end)}'
          : date != null
          ? formatLongDate(date)
          : '';
      return (Icons.event_rounded, title, [if (when.isNotEmpty) when, ?a.location].join(' · '));
    case AssistantActionKind.createChore:
      final due = a.date;
      final day = due == null ? '' : formatDayMonth(due);
      return (Icons.task_alt_rounded, title, due == null ? 'Tarefa nova'.tr : 'Tarefa nova, até $day'.tr);
    case AssistantActionKind.createExpense:
      final amount = formatEuros(a.amountCents ?? 0);
      return (Icons.payments_rounded, title, 'Despesa de $amount, dividida pelos adultos'.tr);
  }
}

/// Pede a fotografia (câmara ou galeria) ou o PDF. Null se a pessoa desistir.
Future<AssistantFile?> pickAssistantFile(BuildContext context, {required bool pdf}) async {
  if (pdf) {
    const group = XTypeGroup(
      label: 'PDF',
      extensions: ['pdf'],
      mimeTypes: ['application/pdf'],
      uniformTypeIdentifiers: ['com.adobe.pdf'],
    );
    final file = await openFile(acceptedTypeGroups: [group]);
    if (file == null) return null;
    return (bytes: await file.readAsBytes(), name: file.name, mediaType: 'application/pdf');
  }

  final picker = ImagePicker();
  final source = picker.supportsImageSource(ImageSource.camera)
      ? await showModalBottomSheet<ImageSource>(
          context: context,
          showDragHandle: true,
          builder: (context) => SafeArea(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                ListTile(
                  leading: const Icon(Icons.photo_camera_rounded),
                  title: Text('Tirar foto'.tr),
                  onTap: () => Navigator.pop(context, ImageSource.camera),
                ),
                ListTile(
                  leading: const Icon(Icons.photo_library_rounded),
                  title: Text('Escolher uma foto'.tr),
                  onTap: () => Navigator.pop(context, ImageSource.gallery),
                ),
              ],
            ),
          ),
        )
      : ImageSource.gallery;
  if (source == null) return null;
  // Reduzida para caber no limite (5 MB) e chegar mais depressa; o talão continua legível.
  final photo = await picker.pickImage(source: source, maxWidth: 2000, maxHeight: 2000, imageQuality: 85);
  if (photo == null) return null;
  final name = photo.name.toLowerCase();
  final mediaType = name.endsWith('.png')
      ? 'image/png'
      : name.endsWith('.webp')
      ? 'image/webp'
      : 'image/jpeg';
  return (bytes: await photo.readAsBytes(), name: photo.name, mediaType: mediaType);
}
