import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../households/invite_link.dart';
import '../l10n/strings.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'dates_pt.dart';
import 'permissions_screen.dart';
import 'theme.dart';

/// Membros da casa: convites e papéis (administradores), perfis de criança com PIN e o código da família (adultos),
/// e sair da casa.
class HouseholdScreen extends StatefulWidget {
  const HouseholdScreen({super.key, required this.householdId, this.pickAbsenceDates = _pickDates});

  final String householdId;

  /// Escolhe os dias de uma ausência (nos testes, devolve-os logo).
  final Future<DateTimeRange?> Function(BuildContext context) pickAbsenceDates;

  static Future<DateTimeRange?> _pickDates(BuildContext context) {
    final today = DateUtils.dateOnly(DateTime.now());
    return showDateRangePicker(
      context: context,
      firstDate: today,
      lastDate: today.add(const Duration(days: 365)),
      helpText: 'Fora entre que dias?'.tr,
      saveText: 'Continuar'.tr,
    );
  }

  @override
  State<HouseholdScreen> createState() => _HouseholdScreenState();
}

class _HouseholdScreenState extends State<HouseholdScreen> {
  HouseholdDetails? _details;
  List<MemberAbsence> _absences = const [];

  /// Os convites que ainda servem (só para quem gere os membros).
  List<Invite> _invites = const [];
  Object? _error;
  String? _familyCode;
  StreamSubscription<HouseholdEvent>? _events;

  ApiClient get _api => context.read<ApiClient>();

  String? get _me => context.read<SessionController>().user?.id;

  @override
  void initState() {
    super.initState();
    _events = context.read<HouseholdRealtime>().events.listen((event) {
      if ((event is MembersChanged && event.householdId == widget.householdId) || event is AbsencesChanged) _load();
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
      final (details, absences) = await (
        _api.getHousehold(widget.householdId),
        _api.getAbsences(widget.householdId),
      ).wait;
      final invites = details.myRole.canManageMembers ? await _api.getInvites(widget.householdId) : const <Invite>[];
      if (mounted) {
        setState(() {
          _details = details;
          _absences = absences;
          _invites = invites;
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
            TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Cancelar'.tr)),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(action)),
          ],
        ),
      ) ==
      true;

  Future<HouseholdRole?> _pickRole(
    String title, {
    HouseholdRole? current,
    List<HouseholdRole> roles = HouseholdRole.values,
  }) => showModalBottomSheet<HouseholdRole>(
    context: context,
    showDragHandle: true,
    builder: (context) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          ListTile(title: Text(title)),
          for (final role in roles)
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

  /// Não há convites para administrador: depois de a pessoa entrar, passa-se a administração.
  Future<void> _invite() async {
    final role = await _pickRole('Convidar como…'.tr, roles: HouseholdRole.invitable);
    if (role == null || !mounted) return;

    try {
      final invite = await _api.createInvite(widget.householdId, role);
      if (!mounted) return;
      await _load();
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (_) => _InviteDialog(invite: invite),
      );
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  /// Anula um convite (o link foi parar a quem não devia): deixa de servir já.
  Future<void> _revokeInvite(Invite invite) async {
    final confirmed = await _confirm(
      'Anular o convite ${invite.code}?'.tr,
      'Quem tiver o código ou o link já não consegue entrar com ele.'.tr,
      'Anular'.tr,
    );
    if (!confirmed) return;
    await _run(() => _api.revokeInvite(widget.householdId, invite.code), done: 'Convite anulado.'.tr);
  }

  Future<void> _changeRole(Member member) async {
    final role = await _pickRole('Papel de ${member.displayName}'.tr, current: member.role);
    if (role == null || role == member.role) return;
    await _run(
      () => _api.updateMemberRole(widget.householdId, member.userId, role),
      done: '${member.displayName} passou a ${role.label.toLowerCase()}.'.tr,
    );
  }

  Future<void> _transferAdmin(Member member) async {
    final confirmed = await _confirm(
      'Passar a administração a ${member.displayName}?'.tr,
      'A administração da casa passa para ${member.displayName} e tu ficas com o papel de Adulto. '
              'Deixas de poder convidar, mudar papéis e tirar pessoas da casa.'
          .tr,
      'Passar'.tr,
    );
    if (!confirmed) return;
    await _run(
      () => _api.transferAdmin(widget.householdId, member.userId),
      done: 'A administração passou para ${member.displayName}.'.tr,
    );
  }

  Future<void> _remove(Member member) async {
    final confirmed = member.isChildProfile
        ? await _confirm(
            'Apagar o perfil de ${member.displayName}?'.tr,
            'O perfil deixa de existir e ${member.displayName} deixa de conseguir entrar. '
                    'As tarefas feitas ficam no histórico.'
                .tr,
            'Apagar perfil'.tr,
          )
        : await _confirm(
            'Tirar ${member.displayName} da casa?'.tr,
            '${member.displayName} deixa de ver a casa. As tarefas que eram suas passam a quem vem a seguir na '
                    'rotação, e deixa de estar nos eventos. As tarefas feitas ficam no histórico.'
                .tr,
            'Tirar da casa'.tr,
          );
    if (!confirmed) return;
    await _run(
      () => _api.removeMember(widget.householdId, member.userId),
      done: member.isChildProfile
          ? 'Perfil de ${member.displayName} apagado.'.tr
          : '${member.displayName} já não está na casa.'.tr,
    );
  }

  /// A próxima ausência (ou a de agora) de um membro.
  MemberAbsence? _absenceOf(Member member) => _absences.where((a) => a.userId == member.userId).firstOrNull;

  Future<void> _markAbsence(Member member) async {
    final range = await widget.pickAbsenceDates(context);
    if (range == null || !mounted) return;
    final reason = await showDialog<String>(
      context: context,
      builder: (_) => _AbsenceReasonDialog(member: member, range: range),
    );
    if (reason == null) return;
    await _run(
      () => _api.createAbsence(
        widget.householdId,
        userId: member.userId,
        start: range.start,
        end: range.end,
        reason: reason,
      ),
      done:
          '${member.displayName} fica fora ${_period(range.start, range.end)}. As tarefas com rotação passam a quem vem a seguir; as outras esperam que volte.'
              .tr,
    );
  }

  Future<void> _cancelAbsence(Member member) async {
    final absence = _absenceOf(member);
    if (absence == null) return;
    await _run(
      () => _api.deleteAbsence(widget.householdId, absence.id),
      done: 'Ausência de ${member.displayName} cancelada.'.tr,
    );
  }

  static String _period(DateTime start, DateTime end) => DateUtils.isSameDay(start, end)
      ? 'a ${formatDayMonthShort(start)}'.tr
      : 'de ${formatDayMonthShort(start)} a ${formatDayMonthShort(end)}'.tr;

  Future<void> _changePin(Member member) async {
    final pin = await showDialog<String>(
      context: context,
      builder: (_) => _PinDialog(title: 'Novo PIN de ${member.displayName}'.tr),
    );
    if (pin == null) return;
    await _run(
      () => _api.setChildPin(widget.householdId, member.userId, pin),
      done: 'O PIN de ${member.displayName} foi mudado e as sessões abertas terminaram.'.tr,
    );
  }

  /// Alguém viu o PIN e entrou noutro aparelho: termina as sessões do perfil em todo o lado.
  Future<void> _signOutChild(Member member) async {
    final confirmed = await _confirm(
      'Terminar as sessões de ${member.displayName}?'.tr,
      '${member.displayName} sai de todos os telemóveis e tablets e volta a entrar com o código da família e o PIN.'.tr,
      'Terminar sessões'.tr,
    );
    if (!confirmed) return;
    await _run(
      () => _api.signOutChild(widget.householdId, member.userId),
      done: 'As sessões de ${member.displayName} terminaram.'.tr,
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
    }, done: 'Perfil de ${profile.name} criado.'.tr);
  }

  Future<void> _showFamilyCode({bool renew = false}) async {
    if (renew) {
      final confirmed = await _confirm(
        'Gerar um código novo?'.tr,
        'O código atual deixa de servir e os perfis das crianças saem de todos os aparelhos: voltam a entrar com o código novo.'
            .tr,
        'Gerar código'.tr,
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
          title: Text('Antes de saíres'.tr),
          content: Text(
            'Mais ninguém administra esta casa. Passa a administração a outra pessoa (no menu ao lado do nome '
                    'dela) e depois já podes sair.'
                .tr,
          ),
          actions: [FilledButton(onPressed: () => Navigator.pop(context), child: Text('Percebi'.tr))],
        ),
      );
      return;
    }

    final confirmed = others.isEmpty
        ? await _confirm(
            'Sair e apagar ${details.name}?'.tr,
            'És o último membro com conta. A casa é apagada com tudo o que tem: listas, tarefas, calendário, '
                    'recompensas e os perfis das crianças.'
                .tr,
            'Sair e apagar'.tr,
          )
        : await _confirm(
            'Sair de ${details.name}?'.tr,
            'Deixas de ver a casa. As tarefas que eram tuas passam a quem vem a seguir na rotação.'.tr,
            'Sair'.tr,
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
      appBar: AppBar(title: Text(details?.name ?? 'Casa'.tr)),
      floatingActionButton: details?.myRole.canManageMembers == true
          ? FloatingActionButton.extended(
              onPressed: _invite,
              icon: const Icon(Icons.person_add_alt_1),
              label: Text('Convidar'.tr),
            )
          : null,
      body: details == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(errorText(_error!)))
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 96),
                children: [
                  for (final member in details.members) _memberTile(member, details, isMe: member.userId == me),
                  if (details.myRole.canManageContent) ...[const SizedBox(height: 12), _childrenCard()],
                  if (details.myRole.canManageMembers && _invites.isNotEmpty) ...[
                    const SizedBox(height: 12),
                    _invitesCard(),
                  ],
                  if (details.myRole.canManageMembers) ...[
                    const SizedBox(height: 12),
                    Card(
                      child: ListTile(
                        leading: const Icon(Icons.tune),
                        title: Text('Permissões'.tr),
                        subtitle: Text('O que adultos, crianças e convidados podem fazer em cada área'.tr),
                        trailing: const Icon(Icons.chevron_right),
                        onTap: () => Navigator.of(context)
                            .push(MaterialPageRoute<void>(builder: (_) => PermissionsScreen(householdId: details.id))),
                      ),
                    ),
                  ],
                  if (meMember != null && !meMember.isChildProfile) ...[
                    const SizedBox(height: 24),
                    Center(
                      child: TextButton.icon(
                        onPressed: () => _leave(details),
                        style: TextButton.styleFrom(foregroundColor: Theme.of(context).colorScheme.error),
                        icon: const Icon(Icons.logout),
                        label: Text('Sair desta casa'.tr),
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
    final absence = _absenceOf(member);
    // As ausências mexem nas tarefas: as dos outros, marca-as quem gere as tarefas; as das crianças, um adulto.
    final managesChores = details.accessTo(HouseholdModule.chores).canManage;
    final canMarkAbsence = managesChores || (isMe && details.myRole != HouseholdRole.child);
    final actions = <(String, String, Future<void> Function(Member))>[
      if (canMarkAbsence && absence == null) ('away', 'Marcar ausência'.tr, _markAbsence),
      if (canMarkAbsence && absence != null) ('back', 'Cancelar ausência'.tr, _cancelAbsence),
      if (!isMe && canManage && !member.isChildProfile) ('role', 'Mudar papel'.tr, _changeRole),
      if (!isMe && canManage && !member.isChildProfile && member.role != HouseholdRole.admin)
        ('admin', 'Passar a administração'.tr, _transferAdmin),
      if (member.isChildProfile && details.myRole.canManageContent) ('pin', 'Mudar PIN'.tr, _changePin),
      if (member.isChildProfile && details.myRole.canManageContent) ('sessions', 'Terminar sessões'.tr, _signOutChild),
      if (!isMe && canManage) ('remove', member.isChildProfile ? 'Apagar perfil'.tr : 'Tirar da casa'.tr, _remove),
    ];
    return Card(
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: colorFromHex(member.color),
          foregroundColor: Colors.white,
          child: Text(member.displayName.isEmpty ? '?' : member.displayName[0].toUpperCase()),
        ),
        title: Text(isMe ? '${member.displayName} (tu)'.tr : member.displayName),
        subtitle: Text(
          [
            member.role.label,
            if (member.isChildProfile) 'entra com PIN'.tr,
            if (absence != null)
              absence.covers(DateTime.now())
                  ? 'fora até ${formatDayMonthShort(absence.endDate)}'.tr
                  : 'fora ${_period(absence.startDate, absence.endDate)}'.tr,
          ].join(' · '),
        ),
        trailing: actions.isEmpty
            ? null
            : PopupMenuButton<String>(
                tooltip: 'Opções de ${member.displayName}'.tr,
                onSelected: (key) => actions.firstWhere((a) => a.$1 == key).$3(member),
                itemBuilder: (_) => [
                  for (final (key, label, _) in actions) PopupMenuItem(value: key, child: Text(label)),
                ],
              ),
      ),
    );
  }

  Widget _invitesCard() {
    final theme = Theme.of(context);
    final localizations = MaterialLocalizations.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 16, 8, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Convites ativos'.tr, style: theme.textTheme.titleMedium),
            for (final invite in _invites)
              ListTile(
                contentPadding: EdgeInsets.zero,
                title: Text('${invite.code} · ${invite.role.label}'),
                subtitle: Text(
                  [
                    'até ${localizations.formatMediumDate(invite.expiresAt)}'.tr,
                    invite.maxUses == null
                        ? 'para várias pessoas'.tr
                        : '${invite.usedCount} de ${invite.maxUses} utilizações'.tr,
                  ].join(' · '),
                ),
                trailing: TextButton(onPressed: () => _revokeInvite(invite), child: Text('Anular'.tr)),
              ),
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
            Text('Crianças sem email'.tr, style: theme.textTheme.titleMedium),
            const SizedBox(height: 4),
            Text(
              'Cria um perfil para cada criança. No telemóvel ou tablet dela, escolhe "Entrar com perfil de criança" '
                      'e escreve o código da família; depois a criança escolhe o nome e escreve o PIN.'
                  .tr,
              style: theme.textTheme.bodySmall,
            ),
            const SizedBox(height: 12),
            if (code != null)
              Row(
                children: [
                  Text('Código da família: '.tr, style: theme.textTheme.bodyMedium),
                  SelectableText(
                    code,
                    style: theme.textTheme.titleLarge?.copyWith(letterSpacing: 3, fontWeight: FontWeight.w600),
                  ),
                  IconButton(
                    tooltip: 'Copiar o código'.tr,
                    icon: const Icon(Icons.copy, size: 20),
                    onPressed: () {
                      Clipboard.setData(ClipboardData(text: code));
                      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Código copiado.'.tr)));
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
                  label: Text('Criar perfil de criança'.tr),
                ),
                if (code == null)
                  TextButton(onPressed: _showFamilyCode, child: Text('Mostrar o código da família'.tr))
                else
                  TextButton(onPressed: () => _showFamilyCode(renew: true), child: Text('Gerar novo código'.tr)),
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
    HouseholdRole.admin => 'Gere a casa, os membros e os convites'.tr,
    HouseholdRole.adult => 'Cria e gere listas e conteúdos'.tr,
    HouseholdRole.child => 'Vê e adiciona artigos, sem apagar listas'.tr,
    HouseholdRole.guest => 'Acesso limitado (babysitter, visitas)'.tr,
  };
}

class _InviteDialog extends StatelessWidget {
  const _InviteDialog({required this.invite});

  final Invite invite;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final expires = MaterialLocalizations.of(context).formatMediumDate(invite.expiresAt);
    final link = inviteLink(invite.code);
    return AlertDialog(
      title: Text('Código de convite'.tr),
      content: SizedBox(
        width: 320,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            // O QR leva o link (ou só o código): lê-se com a câmara do telemóvel ou dentro da app.
            Container(
              color: Colors.white,
              padding: const EdgeInsets.all(8),
              child: QrImageView(data: link ?? invite.code, size: 180, backgroundColor: Colors.white),
            ),
            const SizedBox(height: 12),
            SelectableText(
              invite.code,
              style: theme.textTheme.headlineMedium?.copyWith(letterSpacing: 4, fontWeight: FontWeight.w600),
            ),
            const SizedBox(height: 12),
            Text('Entra como ${invite.role.label.toLowerCase()}. Válido até $expires.'.tr, textAlign: TextAlign.center),
            if (invite.maxUses == 1)
              Text('Serve para uma pessoa.'.tr, textAlign: TextAlign.center, style: theme.textTheme.bodySmall),
          ],
        ),
      ),
      actions: [
        if (link != null)
          TextButton.icon(
            icon: const Icon(Icons.link),
            label: Text('Copiar link'.tr),
            onPressed: () {
              Clipboard.setData(ClipboardData(text: link));
              ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Link copiado.'.tr)));
            },
          ),
        TextButton.icon(
          icon: const Icon(Icons.copy),
          label: Text('Copiar código'.tr),
          onPressed: () {
            Clipboard.setData(ClipboardData(text: invite.code));
            ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Código copiado.'.tr)));
          },
        ),
        FilledButton(onPressed: () => Navigator.pop(context), child: Text('Feito'.tr)),
      ],
    );
  }
}

/// O PIN tem 4 a 6 algarismos; escreve-se duas vezes, para não haver enganos.
String? _validatePin(String? pin) =>
    pin != null && RegExp(r'^\d{4,6}$').hasMatch(pin) ? null : 'O PIN tem de ter 4 a 6 algarismos.'.tr;

List<Widget> _pinFields(TextEditingController pin, TextEditingController repeat) => [
  TextFormField(
    controller: pin,
    decoration: InputDecoration(labelText: 'PIN (4 a 6 algarismos)'.tr),
    keyboardType: TextInputType.number,
    obscureText: true,
    maxLength: 6,
    inputFormatters: [FilteringTextInputFormatter.digitsOnly],
    validator: _validatePin,
  ),
  TextFormField(
    controller: repeat,
    decoration: InputDecoration(labelText: 'Repetir o PIN'.tr),
    keyboardType: TextInputType.number,
    obscureText: true,
    maxLength: 6,
    inputFormatters: [FilteringTextInputFormatter.digitsOnly],
    validator: (v) => v == pin.text ? null : 'Os PINs não são iguais.'.tr,
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
      title: Text('Novo perfil de criança'.tr),
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
                decoration: InputDecoration(labelText: 'Nome'.tr),
                textCapitalization: TextCapitalization.words,
                validator: (v) => (v == null || v.trim().isEmpty) ? 'Indica o nome.'.tr : null,
              ),
              const SizedBox(height: 8),
              ..._pinFields(_pin, _repeat),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(onPressed: _submit, child: Text('Criar perfil'.tr)),
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
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(onPressed: _submit, child: Text('Mudar PIN'.tr)),
      ],
    );
  }
}

/// O motivo (opcional) de uma ausência, depois de escolher os dias.
class _AbsenceReasonDialog extends StatefulWidget {
  const _AbsenceReasonDialog({required this.member, required this.range});

  final Member member;
  final DateTimeRange range;

  @override
  State<_AbsenceReasonDialog> createState() => _AbsenceReasonDialogState();
}

class _AbsenceReasonDialogState extends State<_AbsenceReasonDialog> {
  final _reason = TextEditingController();

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final range = widget.range;
    return AlertDialog(
      title: Text('${widget.member.displayName} vai estar fora'.tr),
      content: SizedBox(
        width: 360,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(_HouseholdScreenState._period(range.start, range.end)),
            const SizedBox(height: 4),
            Text(
              'Enquanto estiver fora, sai das rotações: as tarefas passam a quem vem a seguir e as que não têm a quem passar ficam em pausa até voltar.'
                  .tr,
              style: Theme.of(context).textTheme.bodySmall,
            ),
            TextField(
              controller: _reason,
              decoration: InputDecoration(labelText: 'Motivo (opcional)'.tr, hintText: 'Férias, casa do pai…'.tr),
              maxLength: 200,
            ),
          ],
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(onPressed: () => Navigator.pop(context, _reason.text), child: Text('Marcar ausência'.tr)),
      ],
    );
  }
}
