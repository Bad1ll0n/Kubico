import 'package:flutter/material.dart';

import '../api/models.dart';
import '../l10n/strings.dart';
import '../shopping/voice_input.dart';

/// O artigo a pôr na lista depois de ler um código de barras.
typedef ScannedItem = ({String name, String? category});

/// Confirma o produto lido (ou pede o nome, quando não o conhecemos) antes de o pôr na lista.
class ScannedProductDialog extends StatefulWidget {
  const ScannedProductDialog({super.key, required this.barcode, this.product, this.problem});

  final String barcode;
  final Product? product;

  /// Porque não há produto (não o conhecemos, ou não deu para procurar).
  final String? problem;

  @override
  State<ScannedProductDialog> createState() => _ScannedProductDialogState();
}

class _ScannedProductDialogState extends State<ScannedProductDialog> {
  late final _name = TextEditingController(text: widget.product?.name);
  late final _category = TextEditingController(text: widget.product?.category);

  @override
  void dispose() {
    _name.dispose();
    _category.dispose();
    super.dispose();
  }

  void _submit() {
    final name = _name.text.trim();
    if (name.isEmpty) return;
    final category = _category.text.trim();
    Navigator.pop<ScannedItem>(context, (name: name, category: category.isEmpty ? null : category));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: Text('Adicionar à lista'.tr),
      content: SizedBox(
        width: 400,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (widget.problem != null) ...[Text(widget.problem!), const SizedBox(height: 12)],
            TextField(
              controller: _name,
              autofocus: widget.product == null,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'Nome'.tr),
              onChanged: (_) => setState(() {}),
              onSubmitted: (_) => _submit(),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: _category,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(labelText: 'Secção (opcional)'.tr, hintText: 'ex.: Laticínios'.tr),
              onSubmitted: (_) => _submit(),
            ),
            const SizedBox(height: 12),
            Text('Código ${widget.barcode}'.tr, style: theme.textTheme.bodySmall),
          ],
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(onPressed: _name.text.trim().isEmpty ? null : _submit, child: Text('Adicionar'.tr)),
      ],
    );
  }
}

/// Ouve os artigos ditos ("leite, pão e 2 iogurtes") e devolve o texto, que a pessoa pode corrigir antes.
class DictationDialog extends StatefulWidget {
  const DictationDialog({super.key, required this.voice});

  final VoiceInput voice;

  @override
  State<DictationDialog> createState() => _DictationDialogState();
}

class _DictationDialogState extends State<DictationDialog> {
  final _text = TextEditingController();
  var _listening = false;
  var _unavailable = false;

  /// Porque parou de ouvir antes do tempo (sem autorização, sem rede, não ouviu nada).
  String? _problem;

  /// A pessoa mexeu no texto: o que se continuar a ouvir já não o substitui.
  var _edited = false;

  @override
  void initState() {
    super.initState();
    _listen();
  }

  @override
  void dispose() {
    widget.voice.stop();
    _text.dispose();
    super.dispose();
  }

  Future<void> _listen() async {
    setState(() {
      _listening = true;
      _edited = false;
      _problem = null;
    });
    final before = _text.text.trim();
    final ok = await widget.voice.start(
      onText: (text, _) {
        if (!mounted || _edited) return;
        setState(() => _text.text = [if (before.isNotEmpty) before, text].join(', '));
      },
      onDone: (problem) {
        if (!mounted) return;
        setState(() {
          _listening = false;
          _problem ??= problem;
        });
      },
    );
    if (!ok && mounted) {
      setState(() {
        _listening = false;
        _unavailable = true;
      });
    }
  }

  Future<void> _finish() async {
    await widget.voice.stop();
    if (mounted) Navigator.pop(context, _text.text.trim());
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final items = parseSpokenItems(_text.text);
    return AlertDialog(
      title: Text('Ditar artigos'.tr),
      content: SizedBox(
        width: 400,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                IconButton.filledTonal(
                  tooltip: _listening ? 'A ouvir'.tr : 'Ouvir outra vez'.tr,
                  isSelected: _listening,
                  onPressed: _listening || _unavailable ? null : _listen,
                  icon: const Icon(Icons.mic_none),
                  selectedIcon: const Icon(Icons.mic),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    _unavailable
                        ? 'O ditado não está disponível neste dispositivo. Escreve os artigos aqui em baixo.'.tr
                        : _listening
                        ? 'A ouvir… Diz os artigos, por exemplo: «leite, pão e 2 iogurtes».'.tr
                        : _problem ?? 'Corrige o que for preciso e adiciona.'.tr,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _text,
              minLines: 2,
              maxLines: 4,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(hintText: 'leite, pão e 2 iogurtes'.tr),
              onChanged: (_) => setState(() => _edited = true),
            ),
            if (items.isNotEmpty) ...[
              const SizedBox(height: 12),
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [for (final item in items) Chip(label: Text(item), visualDensity: VisualDensity.compact)],
              ),
            ],
            if (items.isEmpty && !_listening && !_unavailable && _problem == null)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text('Não se percebeu nenhum artigo.'.tr, style: theme.textTheme.bodySmall),
              ),
          ],
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text('Cancelar'.tr)),
        FilledButton(
          onPressed: items.isEmpty ? null : _finish,
          child: Text(items.length <= 1 ? 'Adicionar'.tr : 'Adicionar ${items.length}'.tr),
        ),
      ],
    );
  }
}
