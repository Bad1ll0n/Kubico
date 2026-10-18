import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'theme.dart';

/// Membros da casa: convites e papéis (administradores), perfis de criança com PIN e o código da família (adultos),
/// e sair da casa.
class HouseholdScreen extends StatefulWidget {
  const HouseholdScreen({super.key, required this.householdId});

  final String householdId;

  @override
  State<HouseholdScreen> createState() => _HouseholdScreenState();
}

class _HouseholdScreenState extends State<HouseholdScreen> {
  HouseholdDetails? _details;
  Object? _error;
  String? _familyCode;
  StreamSubscription<HouseholdEvent>? _events;

  ApiClient get _api => context.read<ApiClient>();

  String? get _me => context.read<SessionController>().user?.id;

  @override
  void initState() {
    super.initState();
    _events = context.read<HouseholdRealtime>().events.listen((event) {
      if (event is MembersChanged && event.householdId == widget.householdId) _load();
    });
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final details = await _api.getHousehold(widget.householdId);
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

  /// Faz o pedido, volta a ler os membros e diz o que aconteceu (ou o erro).
  Future<void> _run(Future<void> Function() action, {String? done}) async {
    try {
      await action();
      await _load();
      if (mounted && done != null) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(content: Text(done)));
      }
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<bool> _confirm(String title, String content, String action) async =>
      await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(title),
          content: Text(content),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancelar')),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(action)),
          ],
        ),
      ) ==
      true;

  Future<HouseholdRole?> _pickRole(String title, {HouseholdRole? current}) => showModalBottomSheet<HouseholdRole>(
    context: context,
    showDragHandle: true,
    builder: (context) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          ListTile(title: Text(title)),
          for (final role in HouseholdRole.values)
            ListTile(
              leading: Icon(_roleIcon(role)),
              title: Text(role.label),
              subtitle: Text(_roleDescription(role)),
              selected: role == current,
              onTap: () => Navigator.pop(context, role),
            ),
        ],
      ),
    ),
  );

  Future<void> _invite() async {
    final role = await _pickRole('Convidar como…');
    if (role == null || !mounted) return;

    try {
      final invite = await _api.createInvite(widget.householdId, role);
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (_) => _InviteDialog(invite: invite),
      );
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _changeRole(Member member) async {
    final role = await _pickRole('Papel de ${member.displayName}', current: member.role);
    if (role == null || role == member.role) return;
    await _run(
      () => _api.updateMemberRole(widget.householdId, member.userId, role),
      done: '${member.displayName} passou a ${role.label.toLowerCase()}.',
    );
  }

  Future<void> _transferAdmin(Member member) async {
    final confirmed = await _confirm(
      'Passar a administração a ${member.displayName}?',
      '${member.displayName} fica administrador da casa e tu passas a adulto. '
          'Deixas de poder convidar, mudar papéis e tirar pessoas da casa.',
      'Passar',
    );
    if (!confirmed) return;
    await _run(
      () => _api.transferAdmin(widget.householdId, member.userId),
      done: 'A administração passou para ${member.displayName}.',
    );
  }

  Future<void> _remove(Member member) async {
    final confirmed = member.isChildProfile
        ? await _confirm(
            'Apagar o perfil de ${member.displayName}?',
            'O perfil deixa de existir e ${member.displayName} deixa de conseguir entrar. '
                'As tarefas feitas ficam no histórico.',
            'Apagar perfil',
          )
        : await _confirm(
            'Tirar ${member.displayName} da casa?',
            '${member.displayName} deixa de ver a casa. As tarefas que eram suas passam a quem vem a seguir na '
                'rotação, e deixa de estar nos eventos. As tarefas feitas ficam no histórico.',
            'Tirar da casa',
          );
    if (!confirmed) return;
    await _run(
      () => _api.removeMember(widget.householdId, member.userId),
      done: member.isChildProfile
          ? 'Perfil de ${member.displayName} apagado.'
          : '${member.displayName} já não está na casa.',
    );
  }

  Future<void> _changePin(Member member) async {
    final pin = await showDialog<String>(
      context: context,
      builder: (_) => _PinDialog(title: 'Novo PIN de ${member.displayName}'),
    );
    if (pin == null) return;
    await _run(
      () => _api.setChildPin(widget.householdId, member.userId, pin),
      done: 'O PIN de ${member.displayName} foi mudado.',
    );
  }

  Future<void> _createChild() async {
    final profile = await showDialog<({String name, String pin})>(
      context: context,
      builder: (_) => const _ChildProfileDialog(),
    );
    if (profile == null) return;
    await _run(() async {
      await _api.createChildProfile(widget.householdId, name: profile.name, pin: profile.pin);
      final code = await _api.getFamilyCode(widget.householdId);
      if (mounted) setState(() => _familyCode = code);
    }, done: 'Perfil de ${profile.name} criado.');
  }

  Future<void> _showFamilyCode({bool renew = false}) async {
    if (renew) {
      final confirmed = await _confirm(
        'Gerar um código novo?',
        'O código atual deixa de servir para entrar. Quem já entrou continua com a sessão aberta.',
        'Gerar código',
      );
      if (!confirmed) return;
    }
    try {
      final code = renew
          ? await _api.renewFamilyCode(widget.householdId)
          : await _api.getFamilyCode(widget.householdId);
      if (mounted) setState(() => _familyCode = code);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  Future<void> _leave(HouseholdDetails details) async {
    final me = _me;
    final others = details.members.where((m) => m.userId != me && !m.isChildProfile).toList();
    final isLastAdmin = details.myRole == HouseholdRole.admin && !others.any((m) => m.role == HouseholdRole.admin);
    if (others.isNotEmpty && isLastAdmin) {
      await showDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Antes de saíres'),
          content: const Text(
            'És o único administrador. Passa a administração a outra pessoa (no menu ao lado do nome dela) e '
            'depois já podes sair.',
          ),
          actions: [FilledButton(onPressed: () => Navigator.pop(context), child: const Text('Percebi'))],
        ),
      );
      return;
    }

    final confirmed = others.isEmpty
        ? await _confirm(
            'Sair e apagar ${details.name}?',
            'És o último membro com conta. A casa é apagada com tudo o que tem: listas, tarefas, calendário, '
                'recompensas e os perfis das crianças.',
            'Sair e apagar',
          )
        : await _confirm(
            'Sair de ${details.name}?',
            'Deixas de ver a casa. As tarefas que eram tuas passam a quem vem a seguir na rotação.',
            'Sair',
          );
    if (!confirmed || !mounted) return;

    final session = context.read<SessionController>();
    final navigator = Navigator.of(context);
    try {
      await session.leaveHousehold(widget.householdId);
      navigator.pop();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final details = _details;
    final me = _me;
    final meMember = details?.member(me);
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
                  for (final member in details.members) _memberTile(member, details, isMe: member.userId == me),
                  if (details.myRole.canManageContent) ...[const SizedBox(height: 12), _childrenCard()],
                  if (meMember != null && !meMember.isChildProfile) ...[
                    const SizedBox(height: 24),
                    Center(
                      child: TextButton.icon(
                        onPressed: () => _leave(details),
                        style: TextButton.styleFrom(foregroundColor: Theme.of(context).colorScheme.error),
                        icon: const Icon(Icons.logout),
                        label: const Text('Sair desta casa'),
                      ),
                    ),
                  ],
                ],
              ),
            ),
    );
  }

  Widget _memberTile(Member member, HouseholdDetails details, {required bool isMe}) {
    final canManage = details.myRole.canManageMembers;
    final actions = <(String, String, Future<void> Function(Member))>[
      if (!isMe && canManage && !member.isChildProfile) ('role', 'Mudar papel', _changeRole),
      if (!isMe && canManage && !member.isChildProfile && member.role != HouseholdRole.admin)
        ('admin', 'Passar a administração', _transferAdmin),
      if (member.isChildProfile && details.myRole.canManageContent) ('pin', 'Mudar PIN', _changePin),
      if (!isMe && canManage) ('remove', member.isChildProfile ? 'Apagar perfil' : 'Tirar da casa', _remove),
    ];
    return Card(
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: colorFromHex(member.color),
          foregroundColor: Colors.white,
          child: Text(member.displayName.isEmpty ? '?' : member.displayName[0].toUpperCase()),
        ),
        title: Text(isMe ? '${member.displayName} (tu)' : member.displayName),
        subtitle: Text(member.isChildProfile ? '${member.role.label} · entra com PIN' : member.role.label),
        trailing: actions.isEmpty
            ? null
            : PopupMenuButton<String>(
                tooltip: 'Opções de ${member.displayName}',
                onSelected: (key) => actions.firstWhere((a) => a.$1 == key).$3(member),
                itemBuilder: (_) => [
                  for (final (key, label, _) in actions) PopupMenuItem(value: key, child: Text(label)),
                ],
              ),
      ),
    );
  }

  Widget _childrenCard() {
    final theme = Theme.of(context);
    final code = _familyCode;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Crianças sem email', style: theme.textTheme.titleMedium),
            const SizedBox(height: 4),
            Text(
              'Cria um perfil para cada criança. No telemóvel ou tablet dela, escolhe "Entrar com perfil de criança" '
              'e escreve o código da família; depois a criança escolhe o nome e escreve o PIN.',
              style: theme.textTheme.bodySmall,
            ),
            const SizedBox(height: 12),
            if (code != null)
              Row(
                children: [
                  Text('Código da família: ', style: theme.textTheme.bodyMedium),
                  SelectableText(
                    code,
                    style: theme.textTheme.titleLarge?.copyWith(letterSpacing: 3, fontWeight: FontWeight.w600),
                  ),
                  IconButton(
                    tooltip: 'Copiar o código',
                    icon: const Icon(Icons.copy, size: 20),
                    onPressed: () {
                      Clipboard.setData(ClipboardData(text: code));
                      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Código copiado.')));
                    },
                  ),
                ],
              ),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                FilledButton.tonalIcon(
                  onPressed: _createChild,
                  icon: const Icon(Icons.child_care),
                  label: const Text('Criar perfil de criança'),
                ),
                if (code == null)
                  TextButton(onPressed: _showFamilyCode, child: const Text('Mostrar o código da família'))
                else
                  TextButton(onPressed: () => _showFamilyCode(renew: true), child: const Text('Gerar novo código')),
              ],
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

/// O PIN tem 4 a 6 algarismos; escreve-se duas vezes, para não haver enganos.
String? _validatePin(String? pin) =>
    pin != null && RegExp(r'^\d{4,6}$').hasMatch(pin) ? null : 'O PIN tem de ter 4 a 6 algarismos.';

List<Widget> _pinFields(TextEditingController pin, TextEditingController repeat) => [
  TextFormField(
    controller: pin,
    decoration: const InputDecoration(labelText: 'PIN (4 a 6 algarismos)'),
    keyboardType: TextInputType.number,
    obscureText: true,
    maxLength: 6,
    inputFormatters: [FilteringTextInputFormatter.digitsOnly],
    validator: _validatePin,
  ),
  TextFormField(
    controller: repeat,
    decoration: const InputDecoration(labelText: 'Repetir o PIN'),
    keyboardType: TextInputType.number,
    obscureText: true,
    maxLength: 6,
    inputFormatters: [FilteringTextInputFormatter.digitsOnly],
    validator: (v) => v == pin.text ? null : 'Os PINs não são iguais.',
  ),
];

class _ChildProfileDialog extends StatefulWidget {
  const _ChildProfileDialog();

  @override
  State<_ChildProfileDialog> createState() => _ChildProfileDialogState();
}

class _ChildProfileDialogState extends State<_ChildProfileDialog> {
  final _form = GlobalKey<FormState>();
  final _name = TextEditingController();
  final _pin = TextEditingController();
  final _repeat = TextEditingController();

  @override
  void dispose() {
    _name.dispose();
    _pin.dispose();
    _repeat.dispose();
    super.dispose();
  }

  void _submit() {
    if (_form.currentState!.validate()) Navigator.pop(context, (name: _name.text.trim(), pin: _pin.text));
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Novo perfil de criança'),
      content: Form(
        key: _form,
        child: SizedBox(
          width: 360,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextFormField(
                controller: _name,
                autofocus: true,
                decoration: const InputDecoration(labelText: 'Nome'),
                textCapitalization: TextCapitalization.words,
                validator: (v) => (v == null || v.trim().isEmpty) ? 'Indica o nome.' : null,
              ),
              const SizedBox(height: 8),
              ..._pinFields(_pin, _repeat),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(onPressed: _submit, child: const Text('Criar perfil')),
      ],
    );
  }
}

class _PinDialog extends StatefulWidget {
  const _PinDialog({required this.title});

  final String title;

  @override
  State<_PinDialog> createState() => _PinDialogState();
}

class _PinDialogState extends State<_PinDialog> {
  final _form = GlobalKey<FormState>();
  final _pin = TextEditingController();
  final _repeat = TextEditingController();

  @override
  void dispose() {
    _pin.dispose();
    _repeat.dispose();
    super.dispose();
  }

  void _submit() {
    if (_form.currentState!.validate()) Navigator.pop(context, _pin.text);
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text(widget.title),
      content: Form(
        key: _form,
        child: SizedBox(
          width: 320,
          child: Column(mainAxisSize: MainAxisSize.min, children: _pinFields(_pin, _repeat)),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(onPressed: _submit, child: const Text('Mudar PIN')),
      ],
    );
  }
}
