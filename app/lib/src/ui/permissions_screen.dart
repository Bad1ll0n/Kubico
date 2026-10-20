import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import 'theme.dart';

/// O administrador escolhe, para adultos, crianças e convidados, o que podem fazer em cada área da casa.
class PermissionsScreen extends StatefulWidget {
  const PermissionsScreen({super.key, required this.householdId});

  final String householdId;

  @override
  State<PermissionsScreen> createState() => _PermissionsScreenState();
}

class _PermissionsScreenState extends State<PermissionsScreen> {
  HouseholdPermissions? _saved;
  Map<HouseholdRole, Map<HouseholdModule, ModuleAccess>> _draft = {};
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
      final permissions = await _api.getPermissions(widget.householdId);
      if (mounted) setState(() => _show(permissions));
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  void _show(HouseholdPermissions permissions) {
    _saved = permissions;
    _error = null;
    _draft = {
      for (final r in permissions.roles) r.role: {...r.access},
    };
  }

  Map<HouseholdModule, ModuleAccess> _savedFor(HouseholdRole role) =>
      _saved!.roles.firstWhere((r) => r.role == role).access;

  Map<HouseholdModule, ModuleAccess> _defaultFor(HouseholdRole role) =>
      _saved!.defaults.firstWhere((r) => r.role == role).access;

  bool _same(Map<HouseholdModule, ModuleAccess> a, Map<HouseholdModule, ModuleAccess> b) =>
      HouseholdModule.values.every((m) => a[m] == b[m]);

  bool get _changed => _saved != null && _draft.entries.any((e) => !_same(e.value, _savedFor(e.key)));

  Future<void> _save() async {
    setState(() => _saving = true);
    try {
      final saved = await _api.savePermissions(widget.householdId, [
        for (final e in _draft.entries) RolePermissions(role: e.key, access: e.value),
      ]);
      if (!mounted) return;
      setState(() => _show(saved));
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text('Permissões guardadas.'.tr)));
    } catch (e) {
      if (mounted) showError(context, e);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final saved = _saved;
    return Scaffold(
      appBar: AppBar(
        title: Text('Permissões'.tr),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: FilledButton(onPressed: _changed && !_saving ? _save : null, child: Text('Guardar'.tr)),
          ),
        ],
      ),
      body: saved == null
          ? Center(
              child: _error == null
                  ? const CircularProgressIndicator()
                  : Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(_error.toString()),
                        TextButton(onPressed: _load, child: Text('Tentar de novo'.tr)),
                      ],
                    ),
            )
          : ListView(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 32),
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(4, 4, 4, 12),
                  child: Text(
                    'Escolhe o que cada papel pode fazer em cada área. O administrador pode sempre tudo. '
                            'Uma área que "não aparece" deixa de estar na app dessas pessoas.'
                        .tr,
                    style: Theme.of(context).textTheme.bodyMedium,
                  ),
                ),
                for (final role in _draft.keys) _roleCard(role),
              ],
            ),
    );
  }

  Widget _roleCard(HouseholdRole role) {
    final theme = Theme.of(context);
    final draft = _draft[role]!;
    final usual = _defaultFor(role);
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 8, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(child: Text(_plural(role), style: theme.textTheme.titleMedium)),
                if (!_same(draft, usual))
                  TextButton(
                    onPressed: () => setState(() => _draft[role] = {...usual}),
                    child: Text('Repor o habitual'.tr),
                  ),
              ],
            ),
            for (final module in HouseholdModule.values)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 2),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(module.label, style: theme.textTheme.bodyLarge),
                          Text(_explain(module, draft[module]!), style: theme.textTheme.bodySmall),
                        ],
                      ),
                    ),
                    DropdownButton<ModuleAccess>(
                      key: ValueKey('${role.apiValue}-${module.apiValue}'),
                      value: draft[module],
                      underline: const SizedBox.shrink(),
                      onChanged: (value) => setState(() => draft[module] = value!),
                      items: [
                        for (final level in module.levels) DropdownMenuItem(value: level, child: Text(level.label)),
                      ],
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }

  static String _plural(HouseholdRole role) => switch (role) {
    HouseholdRole.adult => 'Adultos'.tr,
    HouseholdRole.child => 'Crianças'.tr,
    HouseholdRole.guest => 'Convidados'.tr,
    HouseholdRole.admin => 'Administradores'.tr,
  };

  static String _explain(HouseholdModule module, ModuleAccess access) => switch (access) {
    ModuleAccess.none => 'Não aparece na app.'.tr,
    ModuleAccess.view => 'Vê, mas não muda nada.'.tr,
    ModuleAccess.participate => 'Vê e ${module.participateMeans}.'.tr,
    ModuleAccess.manage => 'Cria, altera, apaga e aprova.'.tr,
  };
}
