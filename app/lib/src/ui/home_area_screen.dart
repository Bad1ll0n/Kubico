import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import '../realtime/coalesced_reload.dart';
import '../realtime/household_realtime.dart';
import '../session/session_controller.dart';
import 'chore_history_screen.dart' show pickChorePhoto;
import 'dates_pt.dart';
import 'deferred.dart';
import 'pdf_preview_screen.dart' deferred as pdf;
import 'smart_home_view.dart';
import 'design/components.dart';
import 'theme.dart';

/// A área Casa (F4-07, F4-08, F4-12, F4-13): o quadro de recados, os equipamentos (garantias, manutenções e, num
/// carro, o IUC e a inspeção), o cofre de documentos e os contactos úteis com os aniversários.
class HomeAreaScreen extends StatefulWidget {
  const HomeAreaScreen({required this.household, super.key});

  final HouseholdSummary household;

  @override
  State<HomeAreaScreen> createState() => _HomeAreaScreenState();
}

class _HomeAreaScreenState extends State<HomeAreaScreen> {
  HomeOverview? _home;
  HouseholdDetails? _details;
  Object? _error;
  StreamSubscription<HouseholdEvent>? _events;
  late final _reload = CoalescedReload(_load);

  ApiClient get _api => context.read<ApiClient>();
  String get _householdId => widget.household.id;
  ModuleAccess get _access => widget.household.accessTo(HouseholdModule.home);
  String? get _me => context.read<SessionController>().user?.id;

  @override
  void initState() {
    super.initState();
    _events = context.read<HouseholdRealtime>().events.listen((event) {
      if (event is HomeChanged) _reload.request();
    });
    _load();
  }

  @override
  void dispose() {
    _events?.cancel();
    _reload.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final results = await Future.wait<Object>([_api.getHome(_householdId), _api.getHousehold(_householdId)]);
      if (!mounted) return;
      setState(() {
        _home = results[0] as HomeOverview;
        _details = results[1] as HouseholdDetails;
        _error = null;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _run(Future<void> Function() action) async {
    try {
      await action();
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  String _date(DateTime? d) => d == null ? '—' : '${d.day}/${d.month}/${d.year}';

  @override
  Widget build(BuildContext context) {
    final home = _home;
    // A casa inteligente só aparece a quem gere a Casa (tem o token do Home Assistant).
    final smart = _access.canManage;
    return DefaultTabController(
      length: smart ? 5 : 4,
      child: Scaffold(
        appBar: AppBar(
          title: Text('A casa'.tr),
          bottom: TabBar(
            isScrollable: true,
            tabs: [
              Tab(text: 'Recados'.tr),
              Tab(text: 'Equipamentos'.tr),
              Tab(text: 'Documentos'.tr),
              Tab(text: 'Contactos'.tr),
              if (smart) Tab(text: 'Casa inteligente'.tr),
            ],
          ),
        ),
        body: home == null
            ? (_error == null
                  ? const Center(child: CircularProgressIndicator())
                  : StateView.error(message: errorText(_error!), offline: isOffline(_error!), onRetry: _load))
            : TabBarView(
                children: [
                  _notes(home),
                  _appliances(home),
                  _documents(home),
                  _contacts(home),
                  if (smart) SmartHomeView(householdId: _householdId, members: _details?.members ?? const []),
                ],
              ),
      ),
    );
  }

  // Recados.

  Widget _notes(HomeOverview home) => _Section(
    onAdd: _access.canParticipate ? _addNote : null,
    addLabel: 'Novo recado'.tr,
    empty: 'Ainda não há recados. Deixa um para a casa toda ver no Hoje.'.tr,
    children: [
      for (final n in home.notes)
        Card(
          child: ListTile(
            leading: const Icon(Icons.sticky_note_2_rounded),
            title: Text(n.text),
            subtitle: n.until == null ? null : Text('Até ${_date(n.until)}'.tr),
            trailing: _access.canManage || n.createdByUserId == _me
                ? IconButton(
                    tooltip: 'Tirar o recado'.tr,
                    icon: const Icon(Icons.delete_outline_rounded),
                    onPressed: () => _run(() => _api.deleteHouseNote(_householdId, n.id)),
                  )
                : null,
          ),
        ),
    ],
  );

  Future<void> _addNote() async {
    final text = await _askText(context, 'Novo recado'.tr, 'O recado'.tr, maxLines: 3);
    if (text == null) return;
    await _run(() => _api.addHouseNote(_householdId, text));
  }

  // Equipamentos.

  Widget _appliances(HomeOverview home) => _Section(
    onAdd: _access.canManage ? () => _editAppliance() : null,
    addLabel: 'Novo equipamento'.tr,
    empty: 'Guarda os equipamentos da casa: a garantia, a manutenção e, no carro, o IUC e a inspeção.'.tr,
    children: [
      for (final a in home.appliances)
        Card(
          child: ExpansionTile(
            leading: Icon(a.isCar ? Icons.directions_car_rounded : Icons.kitchen_rounded),
            title: Text(a.name),
            subtitle: Text(
              [
                if (a.room != null) a.room!,
                if (a.plate != null) a.plate!,
                if (a.warrantyUntil != null) 'Garantia até ${_date(a.warrantyUntil)}'.tr,
              ].join(' · '),
            ),
            childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
            expandedCrossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (a.nextIuc != null) Text('IUC: pagar em ${_date(a.nextIuc)}'.tr),
              if (a.nextInspection != null) Text('Próxima inspeção: ${_date(a.nextInspection)}'.tr),
              for (final m in a.maintenance)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.build_rounded),
                  title: Text(m.title),
                  subtitle: Text('A cada ${m.intervalDays} dias · próxima a ${_date(m.nextDue)}'.tr),
                  trailing: _access.canParticipate
                      ? TextButton(
                          onPressed: () => _run(() => _api.maintenanceDone(_householdId, a.id, m.id)),
                          child: Text('Feita'.tr),
                        )
                      : null,
                ),
              _Files(files: a.files, householdId: _householdId),
              if (_access.canManage)
                Wrap(
                  spacing: 8,
                  children: [
                    TextButton.icon(
                      onPressed: () => _attach(a.id),
                      icon: const Icon(Icons.attach_file_rounded),
                      label: Text('Juntar fatura ou manual'.tr),
                    ),
                    TextButton.icon(
                      onPressed: () => _editAppliance(a),
                      icon: const Icon(Icons.edit_rounded),
                      label: Text('Mudar'.tr),
                    ),
                    TextButton.icon(
                      onPressed: () => _run(() => _api.deleteAppliance(_householdId, a.id)),
                      icon: const Icon(Icons.delete_outline_rounded),
                      label: Text('Tirar'.tr),
                    ),
                  ],
                ),
            ],
          ),
        ),
    ],
  );

  Future<void> _attach(String ownerId) async {
    final photo = await pickChorePhoto(context);
    if (photo == null) return;
    await _run(() => _api.uploadHomeFile(_householdId, ownerId, photo.bytes, photo.name));
  }

  Future<void> _editAppliance([Appliance? a]) async {
    final body = await showModalBottomSheet<Map<String, dynamic>>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _ApplianceEditor(appliance: a),
    );
    if (body == null) return;
    await _run(() => _api.saveAppliance(_householdId, body, id: a?.id));
  }

  // Documentos.

  Widget _documents(HomeOverview home) => _Section(
    onAdd: _access.canParticipate ? () => _editDocument() : null,
    addLabel: 'Novo documento'.tr,
    empty: 'Guarda aqui os documentos e a validade: a app avisa antes de caducarem.'.tr,
    children: [
      for (final d in home.documents)
        Card(
          child: ListTile(
            leading: const Icon(Icons.badge_rounded),
            title: Text(d.name),
            subtitle: Text(
              [
                d.kind.label,
                if (d.ownerUserId != null) _details?.member(d.ownerUserId)?.displayName ?? '',
                if (d.number != null) d.number!,
                if (d.expiresOn != null) 'Válido até ${_date(d.expiresOn)}'.tr,
              ].where((s) => s.isNotEmpty).join(' · '),
            ),
            onTap: _access.canManage || d.ownerUserId == _me ? () => _editDocument(d) : null,
          ),
        ),
    ],
  );

  Future<void> _editDocument([HomeDocument? d]) async {
    final details = _details;
    if (details == null) return;
    final body = await showModalBottomSheet<Map<String, dynamic>>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _DocumentEditor(
        document: d,
        me: _me,
        members: _access.canManage
            ? details.members
            : [
                for (final m in details.members)
                  if (m.userId == _me) m,
              ],
      ),
    );
    if (body == null) return;
    await _run(() => _api.saveDocument(_householdId, body, id: d?.id));
  }

  // Contactos e aniversários.

  Widget _contacts(HomeOverview home) => _Section(
    onAdd: _access.canParticipate ? () => _editContact() : null,
    addLabel: 'Novo contacto'.tr,
    empty: 'O médico, a escola, o canalizador… e os aniversários de quem não quer esquecer.'.tr,
    children: [
      if (home.birthdays.isNotEmpty) ...[
        SectionHeader('Próximos aniversários'.tr),
        for (final b in home.birthdays.take(5))
          ListTile(
            leading: const Icon(Icons.cake_rounded),
            title: Text(b.name),
            subtitle: Text(capitalize(formatLongDate(b.date))),
          ),
        SectionHeader('Contactos'.tr),
      ],
      for (final c in home.contacts)
        Card(
          child: ListTile(
            leading: const Icon(Icons.contact_phone_rounded),
            title: Text(c.name),
            subtitle: Text([if (c.role != null) c.role!, if (c.phone != null) c.phone!].join(' · ')),
            onTap: _access.canParticipate ? () => _editContact(c) : null,
          ),
        ),
    ],
  );

  Future<void> _editContact([HouseContact? c]) async {
    final body = await showModalBottomSheet<Map<String, dynamic>>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _ContactEditor(contact: c),
    );
    if (body == null) return;
    await _run(() => _api.saveContact(_householdId, body, id: c?.id));
  }
}

/// Uma lista com um botão para juntar e uma frase quando está vazia.
class _Section extends StatelessWidget {
  const _Section({required this.children, required this.empty, required this.addLabel, this.onAdd});

  final List<Widget> children;
  final String empty;
  final String addLabel;
  final VoidCallback? onAdd;

  @override
  Widget build(BuildContext context) => ListView(
    padding: readablePadding(context, top: 12),
    children: [
      if (onAdd != null)
        Align(
          alignment: Alignment.centerLeft,
          child: FilledButton.tonalIcon(onPressed: onAdd, icon: const Icon(Icons.add_rounded), label: Text(addLabel)),
        ),
      if (children.isEmpty)
        Padding(
          padding: const EdgeInsets.all(16),
          child: Text(empty, textAlign: TextAlign.center),
        )
      else
        ...children,
    ],
  );
}

/// Os ficheiros de um equipamento: as imagens abrem-se num diálogo e os PDF no visualizador (biblioteca diferida).
class _Files extends StatelessWidget {
  const _Files({required this.files, required this.householdId});

  final List<HomeFile> files;
  final String householdId;

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 8,
    children: [
      for (final f in files)
        ActionChip(
          avatar: Icon(f.isImage ? Icons.image_rounded : Icons.picture_as_pdf_rounded, size: 18),
          label: Text(f.fileName),
          onPressed: f.isImage
              ? () async {
                  final api = context.read<ApiClient>();
                  final bytes = await api.getHomeFile(householdId, f.id);
                  if (!context.mounted) return;
                  await showDialog<void>(
                    context: context,
                    builder: (context) => Dialog(child: InteractiveViewer(child: Image.memory(bytes))),
                  );
                }
              : () => pushDeferred<void>(
                  context,
                  pdf.loadLibrary,
                  () => pdf.PdfPreviewScreen(
                    name: f.fileName,
                    load: () => context.read<ApiClient>().getHomeFile(householdId, f.id),
                  ),
                ),
        ),
    ],
  );
}

/// Pede um texto (um recado).
Future<String?> _askText(BuildContext context, String title, String label, {int maxLines = 1}) {
  final controller = TextEditingController();
  return showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(title),
      content: TextField(
        controller: controller,
        autofocus: true,
        maxLines: maxLines,
        textCapitalization: TextCapitalization.sentences,
        decoration: InputDecoration(labelText: label),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(
          onPressed: () {
            final text = controller.text.trim();
            if (text.isNotEmpty) Navigator.pop(context, text);
          },
          child: Text('Guardar'.tr),
        ),
      ],
    ),
  ).whenComplete(controller.dispose);
}

/// Um campo de data com o seletor do sistema.
class _DateField extends StatelessWidget {
  const _DateField({required this.label, required this.value, required this.onChanged});

  final String label;
  final DateTime? value;
  final ValueChanged<DateTime?> onChanged;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      Expanded(
        child: OutlinedButton.icon(
          onPressed: () async {
            final now = clock.now();
            final picked = await showDatePicker(
              context: context,
              initialDate: value ?? now,
              firstDate: DateTime(1950),
              lastDate: DateTime(now.year + 30),
            );
            if (picked != null) onChanged(picked);
          },
          icon: const Icon(Icons.event_rounded),
          label: Text(value == null ? label : '$label: ${value!.day}/${value!.month}/${value!.year}'),
        ),
      ),
      if (value != null)
        IconButton(tooltip: 'Limpar'.tr, onPressed: () => onChanged(null), icon: const Icon(Icons.close_rounded)),
    ],
  );
}

class _ApplianceEditor extends StatefulWidget {
  const _ApplianceEditor({this.appliance});

  final Appliance? appliance;

  @override
  State<_ApplianceEditor> createState() => _ApplianceEditorState();
}

class _ApplianceEditorState extends State<_ApplianceEditor> {
  late final _name = TextEditingController(text: widget.appliance?.name ?? '');
  late final _room = TextEditingController(text: widget.appliance?.room ?? '');
  late final _plate = TextEditingController(text: widget.appliance?.plate ?? '');
  late final _maintenance = TextEditingController(
    text: [for (final m in widget.appliance?.maintenance ?? const <MaintenanceTask>[]) '${m.title}; ${m.intervalDays}']
        .join('\n'),
  );
  late bool _car = widget.appliance?.isCar ?? false;
  late DateTime? _warranty = widget.appliance?.warrantyUntil;
  late DateTime? _registered = widget.appliance?.firstRegistration;

  @override
  void dispose() {
    for (final c in [_name, _room, _plate, _maintenance]) {
      c.dispose();
    }
    super.dispose();
  }

  void _save() {
    if (_name.text.trim().isEmpty) return;
    // Uma manutenção por linha: "Limpar o filtro; 90".
    final maintenance = <Map<String, Object>>[];
    for (final line in _maintenance.text.split('\n')) {
      final cut = line.lastIndexOf(';');
      final days = cut < 0 ? null : int.tryParse(line.substring(cut + 1).trim());
      final title = cut < 0 ? '' : line.substring(0, cut).trim();
      if (days != null && days > 0 && title.isNotEmpty) maintenance.add({'title': title, 'intervalDays': days});
    }
    Navigator.pop(context, {
      'name': _name.text.trim(),
      'kind': _car ? 'Car' : 'General',
      if (_room.text.trim().isNotEmpty) 'room': _room.text.trim(),
      if (_warranty != null) 'warrantyUntil': apiDate(_warranty!),
      if (_car && _plate.text.trim().isNotEmpty) 'plate': _plate.text.trim(),
      if (_car && _registered != null) 'firstRegistration': apiDate(_registered!),
      'maintenance': maintenance,
    });
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.fromLTRB(24, 0, 24, 24 + MediaQuery.viewInsetsOf(context).bottom),
    child: SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          TextField(
            controller: _name,
            decoration: InputDecoration(labelText: 'Nome'.tr),
          ),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text('É um carro'.tr),
            value: _car,
            onChanged: (v) => setState(() => _car = v),
          ),
          if (_car) ...[
            TextField(
              controller: _plate,
              decoration: InputDecoration(labelText: 'Matrícula'.tr),
            ),
            const SizedBox(height: 8),
            _DateField(
              label: 'Primeira matrícula'.tr,
              value: _registered,
              onChanged: (d) => setState(() => _registered = d),
            ),
          ] else
            TextField(
              controller: _room,
              decoration: InputDecoration(labelText: 'Divisão'.tr),
            ),
          const SizedBox(height: 8),
          _DateField(label: 'Garantia até'.tr, value: _warranty, onChanged: (d) => setState(() => _warranty = d)),
          const SizedBox(height: 8),
          TextField(
            controller: _maintenance,
            maxLines: null,
            minLines: 2,
            decoration: InputDecoration(
              labelText: 'Manutenções (uma por linha: "o quê; dias")'.tr,
              hintText: 'Limpar o filtro; 90'.tr,
              alignLabelWithHint: true,
            ),
          ),
          const SizedBox(height: 16),
          FilledButton(onPressed: _save, child: Text('Guardar'.tr)),
        ],
      ),
    ),
  );
}

class _DocumentEditor extends StatefulWidget {
  const _DocumentEditor({required this.members, required this.me, this.document});

  final List<Member> members;
  final String? me;
  final HomeDocument? document;

  @override
  State<_DocumentEditor> createState() => _DocumentEditorState();
}

class _DocumentEditorState extends State<_DocumentEditor> {
  late final _name = TextEditingController(text: widget.document?.name ?? '');
  late final _number = TextEditingController(text: widget.document?.number ?? '');
  late DocumentKind _kind = widget.document?.kind ?? DocumentKind.citizenCard;
  late String? _owner = widget.document == null ? widget.me : widget.document!.ownerUserId;
  late DateTime? _expires = widget.document?.expiresOn;

  @override
  void dispose() {
    _name.dispose();
    _number.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.fromLTRB(24, 0, 24, 24 + MediaQuery.viewInsetsOf(context).bottom),
    child: SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          DropdownButtonFormField<DocumentKind>(
            initialValue: _kind,
            decoration: InputDecoration(labelText: 'Tipo'.tr),
            items: [for (final k in DocumentKind.values) DropdownMenuItem(value: k, child: Text(k.label))],
            onChanged: (v) => setState(() => _kind = v ?? _kind),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _name,
            decoration: InputDecoration(labelText: 'Nome'.tr),
          ),
          const SizedBox(height: 8),
          DropdownButtonFormField<String?>(
            initialValue: widget.members.any((m) => m.userId == _owner) ? _owner : null,
            decoration: InputDecoration(labelText: 'De quem é'.tr),
            items: [
              if (widget.members.length > 1) DropdownMenuItem<String?>(value: null, child: Text('Da casa'.tr)),
              for (final m in widget.members) DropdownMenuItem<String?>(value: m.userId, child: Text(m.displayName)),
            ],
            onChanged: (v) => setState(() => _owner = v),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _number,
            decoration: InputDecoration(labelText: 'Número (guardado cifrado)'.tr),
          ),
          const SizedBox(height: 8),
          _DateField(label: 'Validade'.tr, value: _expires, onChanged: (d) => setState(() => _expires = d)),
          const SizedBox(height: 16),
          FilledButton(
            onPressed: () {
              if (_name.text.trim().isEmpty) return;
              Navigator.pop(context, {
                'kind': _kind.apiValue,
                'name': _name.text.trim(),
                'ownerUserId': _owner,
                if (_number.text.trim().isNotEmpty) 'number': _number.text.trim(),
                if (_expires != null) 'expiresOn': apiDate(_expires!),
              });
            },
            child: Text('Guardar'.tr),
          ),
        ],
      ),
    ),
  );
}

class _ContactEditor extends StatefulWidget {
  const _ContactEditor({this.contact});

  final HouseContact? contact;

  @override
  State<_ContactEditor> createState() => _ContactEditorState();
}

class _ContactEditorState extends State<_ContactEditor> {
  late final _name = TextEditingController(text: widget.contact?.name ?? '');
  late final _role = TextEditingController(text: widget.contact?.role ?? '');
  late final _phone = TextEditingController(text: widget.contact?.phone ?? '');
  late DateTime? _birthday = widget.contact?.birthday;

  @override
  void dispose() {
    for (final c in [_name, _role, _phone]) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.fromLTRB(24, 0, 24, 24 + MediaQuery.viewInsetsOf(context).bottom),
    child: SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          TextField(
            controller: _name,
            decoration: InputDecoration(labelText: 'Nome'.tr),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _role,
            decoration: InputDecoration(labelText: 'Quem é (médico, escola…)'.tr),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _phone,
            keyboardType: TextInputType.phone,
            decoration: InputDecoration(labelText: 'Telefone'.tr),
          ),
          const SizedBox(height: 8),
          _DateField(label: 'Aniversário'.tr, value: _birthday, onChanged: (d) => setState(() => _birthday = d)),
          const SizedBox(height: 16),
          FilledButton(
            onPressed: () {
              if (_name.text.trim().isEmpty) return;
              Navigator.pop(context, {
                'name': _name.text.trim(),
                if (_role.text.trim().isNotEmpty) 'role': _role.text.trim(),
                if (_phone.text.trim().isNotEmpty) 'phone': _phone.text.trim(),
                if (_birthday != null) 'birthday': apiDate(_birthday!),
              });
            },
            child: Text('Guardar'.tr),
          ),
        ],
      ),
    ),
  );
}
