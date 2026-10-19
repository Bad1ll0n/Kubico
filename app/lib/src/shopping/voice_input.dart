import 'package:flutter/foundation.dart';
import 'package:speech_to_text/speech_to_text.dart';

/// Ditado: ouve o que a pessoa diz e vai devolvendo o texto.
abstract class VoiceInput {
  /// Começa a ouvir em português. Devolve false se o ditado não estiver disponível (sem microfone, sem autorização,
  /// ou o sistema não o suporta). [onText] recebe o que já se percebeu (no fim, com `isFinal`); [onDone] quando
  /// deixa de ouvir, com o problema pronto a mostrar se parou por erro.
  Future<bool> start({
    required void Function(String text, bool isFinal) onText,
    required void Function(String? problem) onDone,
  });

  Future<void> stop();
}

/// Ditado do sistema (Android, iPhone, Mac, Windows; na web, o do navegador).
class SpeechVoiceInput implements VoiceInput {
  SpeechVoiceInput._();

  static final shared = SpeechVoiceInput._();

  final _speech = SpeechToText();
  bool? _ready;
  String? _localeId;
  void Function(String? problem)? _onDone;

  @override
  Future<bool> start({
    required void Function(String text, bool isFinal) onText,
    required void Function(String? problem) onDone,
  }) async {
    _onDone = onDone;
    try {
      _ready ??= await _speech.initialize(
        onStatus: (status) {
          if (status == SpeechToText.doneStatus || status == SpeechToText.notListeningStatus) _onDone?.call(null);
        },
        onError: (error) => _onDone?.call(describeSpeechError(error.errorMsg)),
      );
      if (_ready != true) return false;
      _localeId ??= await _portuguese();
      await _speech.listen(
        onResult: (result) => onText(result.recognizedWords, result.finalResult),
        listenOptions: SpeechListenOptions(
          localeId: _localeId,
          listenMode: ListenMode.dictation,
          pauseFor: const Duration(seconds: 3),
          listenFor: const Duration(seconds: 30),
          cancelOnError: true,
        ),
      );
      return true;
    } catch (_) {
      _ready = false;
      return false;
    }
  }

  @override
  Future<void> stop() async {
    _onDone = null;
    if (_speech.isListening) await _speech.stop();
  }

  /// Português de Portugal, se houver; senão outro português; senão o idioma do sistema.
  Future<String?> _portuguese() async {
    final ids = [for (final l in await _speech.locales()) l.localeId];
    String norm(String id) => id.replaceAll('-', '_').toLowerCase();
    return ids.where((id) => norm(id) == 'pt_pt').firstOrNull ??
        ids.where((id) => norm(id).startsWith('pt')).firstOrNull ??
        (kIsWeb ? 'pt-PT' : null);
  }
}

/// O erro do reconhecimento de voz (Android, iPhone ou navegador) explicado a quem está a usar a app.
String describeSpeechError(String code) {
  final c = code.toLowerCase();
  if (c.contains('permission') || c.contains('not-allowed') || c.contains('not_allowed') || c.contains('denied')) {
    return 'O Kubico não tem autorização para usar o microfone. Dá-a nas definições, ou escreve os artigos aqui em baixo.';
  }
  if (c.contains('no_match') || c.contains('no-match') || c.contains('no-speech') || c.contains('timeout')) {
    return 'Não se ouviu nada. Toca no microfone para tentar outra vez.';
  }
  if (c.contains('network')) return 'O ditado precisa de internet. Escreve os artigos aqui em baixo.';
  return 'Não foi possível ouvir. Toca no microfone para tentar outra vez, ou escreve os artigos.';
}

/// "leite, pão e duas dúzias de ovos" → ["leite", "pão", "24 ovos"]. Separa por vírgulas, "e" e "mais"; os números
/// ditos por extenso no início passam a algarismos ("dois iogurtes" → "2 iogurtes") e "um"/"uma" desaparece.
List<String> parseSpokenItems(String text) => [
  for (final part in text.split(RegExp(r'\s*(?<!\d)[,;.](?!\d)\s*|\s+(?:e|mais|também)\s+', caseSensitive: false)))
    if (_spokenQuantity(part.trim()) case final item when item.isNotEmpty) item,
];

const _dozens = {'meia dúzia de': '6', 'uma dúzia de': '12', 'duas dúzias de': '24'};
const _numbers = {
  'dois': '2',
  'duas': '2',
  'três': '3',
  'tres': '3',
  'quatro': '4',
  'cinco': '5',
  'seis': '6',
  'sete': '7',
  'oito': '8',
  'nove': '9',
  'dez': '10',
  'doze': '12',
};

String _spokenQuantity(String part) {
  final lower = part.toLowerCase();
  for (final MapEntry(:key, :value) in _dozens.entries) {
    if (lower.startsWith('$key ')) return '$value ${part.substring(key.length + 1).trim()}';
  }
  final space = part.indexOf(' ');
  if (space < 0) return part;
  final first = lower.substring(0, space);
  final rest = part.substring(space + 1).trim();
  if (first == 'um' || first == 'uma') return rest;
  final number = _numbers[first];
  return number == null ? part : '$number $rest';
}
