import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import 'theme.dart';

/// Membros da casa e criação de convites (só administradores).
class HouseholdScreen extends StatefulWidget {
  const HouseholdScreen({super.key, required this.householdId});

  final String householdId;

  @override
  State<HouseholdScreen> createState() => _HouseholdScreenState();
}

class _HouseholdScreenState extends State<HouseholdScreen> {
  HouseholdDetails? _details;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final details = await context.read<ApiClient>().getHousehold(widget.householdId);
      if (mounted) {
        setState(() {
          _details = details;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _invite() async {
    final role = await showModalBottomSheet<HouseholdRole>(
      context: context,
      showDragHandle: true,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const ListTile(title: Text('Convidar como…')),
            for (final role in HouseholdRole.values)
              ListTile(
                leading: Icon(_roleIcon(role)),
                title: Text(role.label),
                subtitle: Text(_roleDescription(role)),
                onTap: () => Navigator.pop(context, role),
              ),
          ],
        ),
      ),
    );
    if (role == null || !mounted) return;

    try {
      final invite = await context.read<ApiClient>().createInvite(widget.householdId, role);
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (_) => _InviteDialog(invite: invite),
      );
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final details = _details;
    return Scaffold(
      appBar: AppBar(title: Text(details?.name ?? 'Casa')),
      floatingActionButton: details?.myRole.canManageMembers == true
          ? FloatingActionButton.extended(
              onPressed: _invite,
              icon: const Icon(Icons.person_add_alt_1),
              label: const Text('Convidar'),
            )
          : null,
      body: details == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error.toString()))
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
                children: [
                  for (final member in details.members)
                    Card(
                      child: ListTile(
                        leading: CircleAvatar(
                          backgroundColor: colorFromHex(member.color),
                          foregroundColor: Colors.white,
                          child: Text(member.displayName.isEmpty ? '?' : member.displayName[0].toUpperCase()),
                        ),
                        title: Text(member.displayName),
                        subtitle: Text(member.role.label),
                      ),
                    ),
                ],
              ),
            ),
    );
  }

  static IconData _roleIcon(HouseholdRole role) => switch (role) {
    HouseholdRole.admin => Icons.admin_panel_settings_outlined,
    HouseholdRole.adult => Icons.person_outline,
    HouseholdRole.child => Icons.child_care,
    HouseholdRole.guest => Icons.badge_outlined,
  };

  static String _roleDescription(HouseholdRole role) => switch (role) {
    HouseholdRole.admin => 'Gere a casa, os membros e os convites',
    HouseholdRole.adult => 'Cria e gere listas e conteúdos',
    HouseholdRole.child => 'Vê e adiciona artigos, sem apagar listas',
    HouseholdRole.guest => 'Acesso limitado (babysitter, visitas)',
  };
}

class _InviteDialog extends StatelessWidget {
  const _InviteDialog({required this.invite});

  final Invite invite;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final expires = MaterialLocalizations.of(context).formatMediumDate(invite.expiresAt);
    return AlertDialog(
      title: const Text('Código de convite'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          SelectableText(
            invite.code,
            style: theme.textTheme.headlineMedium?.copyWith(letterSpacing: 4, fontWeight: FontWeight.w600),
          ),
          const SizedBox(height: 12),
          Text('Entra como ${invite.role.label.toLowerCase()}. Válido até $expires.', textAlign: TextAlign.center),
        ],
      ),
      actions: [
        TextButton.icon(
          icon: const Icon(Icons.copy),
          label: const Text('Copiar'),
          onPressed: () {
            Clipboard.setData(ClipboardData(text: invite.code));
            ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Código copiado.')));
          },
        ),
        FilledButton(onPressed: () => Navigator.pop(context), child: const Text('Feito')),
      ],
    );
  }
}
