import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import 'dates_pt.dart';
import 'design/components.dart';
import 'theme.dart';

/// A casa inteligente (F4-16c), na área Casa para quem a gere: ligar o Home Assistant (endereço e token de longa
/// duração) e as regras "quando a máquina de lavar acabar, criar a tarefa estender a roupa".
class SmartHomeView extends StatefulWidget {
  const SmartHomeView({required this.householdId, required this.members, super.key});

  final String householdId;
  final List<Member> members;

  @override
  State<SmartHomeView> createState() => _SmartHomeViewState();
}

class _SmartHomeViewState extends State<SmartHomeView> {
  late final ApiClient _api = context.read<ApiClient>();
  final _url = TextEditingController();
  final _token = TextEditingController();
  SmartHome? _home;
  Object? _error;
  bool _busy = false;
  String? _connectError;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _url.dispose();
    _token.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final home = await _api.getSmartHome(widget.householdId);
      if (mounted) {
        setState(() {
          _home = home;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() => _busy = true);
    try {
      await action();
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _connect() async {
    setState(() {
      _busy = true;
      _connectError = null;
    });
    try {
      final home = await _api.connectSmartHome(widget.householdId, _url.text.trim(), _token.text.trim());
      _token.clear();
      if (mounted) setState(() => _home = home);
    } catch (e) {
      if (mounted) setState(() => _connectError = errorText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _editRule([SmartHomeRule? rule]) async {
    List<SmartHomeEntity> entities;
    try {
      entities = await _api.getSmartHomeEntities(widget.householdId);
    } catch (e) {
      if (mounted) showError(context, e);
      return;
    }
    if (!mounted) return;
    final saved = await showModalBottomSheet<Object>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _RuleEditor(rule: rule, entities: entities, members: widget.members),
    );
    switch (saved) {
      case final SmartHomeRule changed:
        await _run(() => _api.saveSmartHomeRule(widget.householdId, changed, isNew: rule == null));
      case _RuleEditor.delete when rule != null:
        await _run(() => _api.deleteSmartHomeRule(widget.householdId, rule.id));
    }
  }

  @override
  Widget build(BuildContext context) {
    final home = _home;
    if (home == null) {
      return _error == null
          ? const Center(child: CircularProgressIndicator())
          : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load);
    }
    final theme = Theme.of(context);
    return ListView(
      padding: readablePadding(context, top: 16, bottom: 32),
      children: [
        if (!home.connected) ...[
          Text('Liga o Home Assistant da casa'.tr, style: theme.textTheme.titleMedium),
          const SizedBox(height: 4),
          Text(
            'No Home Assistant, abre o teu perfil, Segurança, e cria um token de longa duração. '
                    'O token fica guardado cifrado e nunca volta a aparecer.'
                .tr,
            style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurfaceVariant),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _url,
            keyboardType: TextInputType.url,
            decoration: InputDecoration(labelText: 'Endereço'.tr, hintText: 'http://192.168.1.50:8123'),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _token,
            obscureText: true,
            decoration: InputDecoration(labelText: 'Token de longa duração'.tr, errorText: _connectError),
          ),
          const SizedBox(height: 12),
          FilledButton.icon(
            onPressed: _busy ? null : _connect,
            icon: const Icon(Icons.link_rounded),
            label: Text('Ligar'.tr),
          ),
        ] else ...[
          Card(
            child: ListTile(
              leading: Icon(
                home.lastError == null ? Icons.check_circle_rounded : Icons.warning_amber_rounded,
                color: home.lastError == null ? context.kubico.success : context.kubico.warning,
              ),
              title: Text(home.baseUrl ?? ''),
              subtitle: Text(_status(home)),
              trailing: PopupMenuButton<String>(
                tooltip: 'Mais opções'.tr,
                onSelected: (value) => value == 'check'
                    ? _run(() => _api.checkSmartHome(widget.householdId))
                    : _run(() => _api.disconnectSmartHome(widget.householdId)),
                itemBuilder: (_) => [
                  PopupMenuItem(value: 'check', child: Text('Verificar agora'.tr)),
                  PopupMenuItem(value: 'disconnect', child: Text('Desligar'.tr)),
                ],
              ),
            ),
          ),
          const SizedBox(height: 16),
          SectionHeader('Regras'.tr),
          if (home.rules.isEmpty)
            StateView(
              inList: true,
              icon: Icons.sensors_rounded,
              title: 'Ainda não há regras'.tr,
              message: 'Por exemplo: quando a máquina de lavar acabar, criar a tarefa «Estender a roupa».'.tr,
            ),
          for (final rule in home.rules)
            Card(
              child: SwitchListTile(
                value: rule.enabled,
                onChanged: _busy
                    ? null
                    : (on) => _run(() => _api.saveSmartHomeRule(widget.householdId, rule.copyWith(enabled: on))),
                title: Text(rule.choreTitle),
                subtitle: Text(_describe(rule)),
                secondary: IconButton(
                  tooltip: 'Editar a regra'.tr,
                  icon: const Icon(Icons.edit_rounded),
                  onPressed: () => _editRule(rule),
                ),
              ),
            ),
          const SizedBox(height: 8),
          Align(
            alignment: Alignment.centerLeft,
            child: OutlinedButton.icon(
              onPressed: _busy ? null : () => _editRule(),
              icon: const Icon(Icons.add_rounded),
              label: Text('Nova regra'.tr),
            ),
          ),
        ],
      ],
    );
  }

  String _status(SmartHome home) {
    final error = home.lastError;
    if (error != null) return error;
    final checked = home.lastCheckedAt;
    if (checked == null) return 'Ligado'.tr;
    final when = formatTime(checked);
    return 'Ligado · verificado às $when'.tr;
  }

  String _describe(SmartHomeRule rule) {
    final entity = rule.entityName ?? rule.entityId;
    final state = rule.toState;
    final who = widget.members.where((m) => m.userId == rule.assigneeUserId).firstOrNull?.displayName;
    final when = 'Quando «$entity» ficar «$state»'.tr;
    return who == null ? when : '$when · $who';
  }
}

class _RuleEditor extends StatefulWidget {
  const _RuleEditor({required this.entities, required this.members, this.rule});

  /// O que o editor devolve quando se apaga a regra.
  static const delete = 'apagar';

  final SmartHomeRule? rule;
  final List<SmartHomeEntity> entities;
  final List<Member> members;

  @override
  State<_RuleEditor> createState() => _RuleEditorState();
}

class _RuleEditorState extends State<_RuleEditor> {
  late String? _entity = widget.rule?.entityId ?? widget.entities.firstOrNull?.entityId;
  late final _state = TextEditingController(text: widget.rule?.toState);
  late final _title = TextEditingController(text: widget.rule?.choreTitle);
  late String? _assignee = widget.rule?.assigneeUserId;
  late int _points = widget.rule?.points ?? 0;
  String? _error;

  @override
  void dispose() {
    _state.dispose();
    _title.dispose();
    super.dispose();
  }

  SmartHomeEntity? get _chosen => widget.entities.where((e) => e.entityId == _entity).firstOrNull;

  void _save() {
    final entity = _entity;
    if (entity == null || _state.text.trim().isEmpty || _title.text.trim().isEmpty) {
      setState(() => _error = 'Escolhe a entidade e escreve o estado e a tarefa.'.tr);
      return;
    }
    Navigator.pop(
      context,
      SmartHomeRule(
        id: widget.rule?.id ?? '',
        entityId: entity,
        entityName: _chosen?.name ?? widget.rule?.entityName,
        toState: _state.text.trim(),
        choreTitle: _title.text.trim(),
        assigneeUserId: _assignee,
        points: _points,
        enabled: widget.rule?.enabled ?? true,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final current = _chosen?.state;
    return Padding(
      padding: EdgeInsets.fromLTRB(24, 0, 24, 24 + MediaQuery.viewInsetsOf(context).bottom),
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              widget.rule == null ? 'Nova regra'.tr : 'Editar a regra'.tr,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            DropdownButtonFormField<String>(
              initialValue: widget.entities.any((e) => e.entityId == _entity) ? _entity : null,
              isExpanded: true,
              decoration: InputDecoration(labelText: 'Quando esta entidade'.tr),
              items: [
                for (final e in widget.entities)
                  DropdownMenuItem(
                    value: e.entityId,
                    child: Text(e.label, overflow: TextOverflow.ellipsis),
                  ),
              ],
              onChanged: (value) => setState(() => _entity = value),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: _state,
              decoration: InputDecoration(
                labelText: 'ficar neste estado'.tr,
                helperText: current == null ? null : 'Agora está «$current»'.tr,
              ),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: _title,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'criar a tarefa'.tr, hintText: 'Estender a roupa'.tr),
            ),
            const SizedBox(height: 8),
            DropdownButtonFormField<String?>(
              initialValue: widget.members.any((m) => m.userId == _assignee) ? _assignee : null,
              decoration: InputDecoration(labelText: 'para'.tr),
              items: [
                DropdownMenuItem<String?>(value: null, child: Text('Ninguém em especial'.tr)),
                for (final m in widget.members) DropdownMenuItem<String?>(value: m.userId, child: Text(m.displayName)),
              ],
              onChanged: (value) => setState(() => _assignee = value),
            ),
            const SizedBox(height: 8),
            DropdownButtonFormField<int>(
              initialValue: _points,
              decoration: InputDecoration(labelText: 'Pontos'.tr),
              items: [
                for (final p in const [0, 1, 2, 3, 5, 10]) DropdownMenuItem(value: p, child: Text('$p')),
              ],
              onChanged: (value) => setState(() => _points = value ?? 0),
            ),
            if (_error != null)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
              ),
            const SizedBox(height: 16),
            FilledButton(onPressed: _save, child: Text('Guardar'.tr)),
            if (widget.rule != null)
              TextButton(onPressed: () => Navigator.pop(context, _RuleEditor.delete), child: Text('Apagar a regra'.tr)),
          ],
        ),
      ),
    );
  }
}
