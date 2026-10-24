import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../l10n/strings.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// Uma foto escolhida ou tirada, pronta a enviar.
typedef PickedPhoto = ({Uint8List bytes, String name});

/// Pede uma foto do trabalho feito: com a câmara, se houver, ou da galeria. Null se a pessoa desistir.
Future<PickedPhoto?> pickChorePhoto(BuildContext context) async {
  final picker = ImagePicker();
  final camera = picker.supportsImageSource(ImageSource.camera);
  final source = await showModalBottomSheet<ImageSource>(
    context: context,
    showDragHandle: true,
    builder: (context) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(24, 0, 24, 8),
            child: Text('Esta tarefa pede uma foto do trabalho feito.'.tr),
          ),
          if (camera)
            ListTile(
              leading: const Icon(Icons.photo_camera_outlined),
              title: Text('Tirar foto'.tr),
              onTap: () => Navigator.pop(context, ImageSource.camera),
            ),
          ListTile(
            leading: const Icon(Icons.photo_library_outlined),
            title: Text('Escolher uma foto'.tr),
            onTap: () => Navigator.pop(context, ImageSource.gallery),
          ),
        ],
      ),
    ),
  );
  if (source == null) return null;
  // Mais pequena, para enviar depressa e ocupar pouco.
  final file = await picker.pickImage(source: source, maxWidth: 1600, maxHeight: 1600, imageQuality: 80);
  if (file == null) return null;
  return (bytes: await file.readAsBytes(), name: file.name);
}

/// Uma foto de prova, lida do servidor com a sessão (as últimas vistas ficam guardadas na memória).
class ChorePhotoImage extends StatefulWidget {
  const ChorePhotoImage({
    super.key,
    required this.householdId,
    required this.photoId,
    this.size,
    this.fit = BoxFit.cover,
  });

  final String householdId;
  final String photoId;
  final double? size;
  final BoxFit fit;

  /// As fotos já vistas (as mais recentes), para não as voltar a pedir.
  static final _cache = <String, Uint8List>{};

  @override
  State<ChorePhotoImage> createState() => _ChorePhotoImageState();
}

class _ChorePhotoImageState extends State<ChorePhotoImage> {
  late final Future<Uint8List> _bytes = _load();

  Future<Uint8List> _load() async {
    final cache = ChorePhotoImage._cache;
    final cached = cache.remove(widget.photoId);
    final bytes = cached ?? await context.read<ApiClient>().getChorePhoto(widget.householdId, widget.photoId);
    cache[widget.photoId] = bytes;
    if (cache.length > 50) cache.remove(cache.keys.first);
    return bytes;
  }

  @override
  Widget build(BuildContext context) => FutureBuilder(
    future: _bytes,
    builder: (context, snapshot) => SizedBox(
      width: widget.size,
      height: widget.size,
      child: switch (snapshot) {
        AsyncSnapshot(:final data?) => Image.memory(data, fit: widget.fit),
        AsyncSnapshot(hasError: true) => const Icon(Icons.broken_image_outlined),
        _ => const Center(child: CircularProgressIndicator(strokeWidth: 2)),
      },
    ),
  );
}

/// Quem fez o quê nos últimos 30 dias, com as fotos de prova. Quem gere as tarefas pode não aceitar uma conclusão.
class ChoreHistoryScreen extends StatefulWidget {
  const ChoreHistoryScreen({super.key, required this.household});

  final HouseholdSummary household;

  @override
  State<ChoreHistoryScreen> createState() => _ChoreHistoryScreenState();
}

class _ChoreHistoryScreenState extends State<ChoreHistoryScreen> {
  List<ChoreHistoryItem>? _items;
  HouseholdDetails? _details;
  Object? _error;

  ApiClient get _api => context.read<ApiClient>();
  bool get _canReview => widget.household.accessTo(HouseholdModule.chores).canManage;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final (items, details) = (
        await _api.getChoreHistory(widget.household.id),
        await _api.getHousehold(widget.household.id),
      );
      if (mounted) {
        setState(() {
          _items = items;
          _details = details;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _reject(ChoreHistoryItem item) async {
    final name = _details?.member(item.userId)?.displayName ?? 'Quem a fez'.tr;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Não aceitar «${item.choreTitle}»?'.tr),
        content: Text(
          '${item.points > 0 ? 'Os ${item.points} pontos deixam de contar e a'.tr : 'A'.tr} tarefa volta para $name, '
                  'para hoje, com um aviso.'
              .tr,
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text('Cancelar'.tr)),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text('Não aceitar'.tr)),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    try {
      ChoreHistoryItem updated;
      try {
        updated = await _api.rejectChoreCompletion(widget.household.id, item.id);
      } on ApiException catch (e) {
        // Os pontos já foram trocados por recompensas: pergunta antes de deixar a pessoa a dever.
        if (!e.isPointsSpent || !mounted) rethrow;
        if (!await confirmPointsDebt(context, e)) return;
        updated = await _api.rejectChoreCompletion(widget.household.id, item.id, force: true);
      }
      if (!mounted) return;
      setState(() => _items = [for (final i in _items!) i.id == updated.id ? updated : i]);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  void _openPhoto(ChoreHistoryItem item) => showDialog<void>(
    context: context,
    builder: (context) => Dialog(
      clipBehavior: Clip.antiAlias,
      child: Stack(
        children: [
          InteractiveViewer(
            child: ChorePhotoImage(householdId: widget.household.id, photoId: item.photoId!, fit: BoxFit.contain),
          ),
          Positioned(
            top: 4,
            right: 4,
            child: IconButton.filledTonal(
              tooltip: 'Fechar'.tr,
              onPressed: () => Navigator.pop(context),
              icon: const Icon(Icons.close),
            ),
          ),
        ],
      ),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final items = _items;
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: Text('Histórico das tarefas'.tr)),
      body: items == null
          ? Center(
              child: _error == null
                  ? const CircularProgressIndicator()
                  : Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(errorText(_error!)),
                        TextButton(onPressed: _load, child: Text('Tentar de novo'.tr)),
                      ],
                    ),
            )
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
                children: [
                  if (items.isEmpty)
                    Padding(
                      padding: const EdgeInsets.all(32),
                      child: Text('Ainda ninguém fez tarefas nos últimos 30 dias.'.tr, textAlign: TextAlign.center),
                    ),
                  for (final (i, item) in items.indexed) ...[
                    if (i == 0 || !_sameDay(items[i - 1].completedAt, item.completedAt))
                      Padding(
                        padding: const EdgeInsets.fromLTRB(8, 16, 8, 4),
                        child: Text(formatShortDate(item.completedAt), style: theme.textTheme.titleSmall),
                      ),
                    _tile(item),
                  ],
                ],
              ),
            ),
    );
  }

  Widget _tile(ChoreHistoryItem item) {
    final theme = Theme.of(context);
    final member = _details?.member(item.userId);
    final name = member?.displayName ?? 'Antigo membro'.tr;
    return Card(
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: member == null ? null : colorFromHex(member.color),
          foregroundColor: Colors.white,
          child: Text(name.isEmpty ? '?' : name[0].toUpperCase()),
        ),
        title: Text(
          item.choreTitle,
          style: item.rejected
              ? TextStyle(decoration: TextDecoration.lineThrough, color: theme.colorScheme.outline)
              : null,
        ),
        subtitle: Text(
          [
            name,
            formatTime(item.completedAt),
            if (item.points > 0) '${item.points} pts',
            if (item.rejected) 'não aceite'.tr,
          ].join(' · '),
        ),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (item.photoId != null)
              InkWell(
                onTap: () => _openPhoto(item),
                borderRadius: BorderRadius.circular(8),
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(8),
                  child: ChorePhotoImage(householdId: widget.household.id, photoId: item.photoId!, size: 48),
                ),
              ),
            if (_canReview && !item.rejected)
              PopupMenuButton<String>(
                tooltip: 'Opções de ${item.choreTitle}'.tr,
                onSelected: (_) => _reject(item),
                itemBuilder: (_) => [PopupMenuItem(value: 'reject', child: Text('Não aceitar'.tr))],
              ),
          ],
        ),
      ),
    );
  }

  static bool _sameDay(DateTime a, DateTime b) => a.year == b.year && a.month == b.month && a.day == b.day;
}
