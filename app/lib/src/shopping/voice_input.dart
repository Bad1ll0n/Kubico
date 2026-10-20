import 'package:flutter/foundation.dart';
import 'package:speech_to_text/speech_to_text.dart';

import '../l10n/strings.dart';

/// Ditado: ouve o que a pessoa diz e vai devolvendo o texto.
abstract class VoiceInput {
  /// Começa a ouvir na língua da app. Devolve false se o ditado não estiver disponível (sem microfone, sem autorização,
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

  /// O reconhecimento a usar para cada língua da app (null: o do sistema).
  final _localeIds = <String, String?>{};
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
      final language = AppLanguage.code;
      final localeId = _localeIds.containsKey(language)
          ? _localeIds[language]
          : _localeIds[language] = await _localeFor(language);
      await _speech.listen(
        onResult: (result) => onText(result.recognizedWords, result.finalResult),
        listenOptions: SpeechListenOptions(
          localeId: localeId,
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

  /// Português de Portugal (ou inglês britânico), se houver; senão outra variante da língua; senão a do sistema.
  Future<String?> _localeFor(String language) async {
    final ids = [for (final l in await _speech.locales()) l.localeId];
    String norm(String id) => id.replaceAll('-', '_').toLowerCase();
    final preferred = language == AppLanguage.english ? 'en_gb' : 'pt_pt';
    return ids.where((id) => norm(id) == preferred).firstOrNull ??
        ids.where((id) => norm(id).startsWith(language)).firstOrNull ??
        (kIsWeb ? (language == AppLanguage.english ? 'en-GB' : 'pt-PT') : null);
  }
}

/// O erro do reconhecimento de voz (Android, iPhone ou navegador) explicado a quem está a usar a app.
String describeSpeechError(String code) {
  final c = code.toLowerCase();
  if (c.contains('permission') || c.contains('not-allowed') || c.contains('not_allowed') || c.contains('denied')) {
    return 'O Kubico não tem autorização para usar o microfone. Dá-a nas definições, ou escreve os artigos aqui em baixo.'
        .tr;
  }
  if (c.contains('no_match') || c.contains('no-match') || c.contains('no-speech') || c.contains('timeout')) {
    return 'Não se ouviu nada. Toca no microfone para tentar outra vez.'.tr;
  }
  if (c.contains('network')) return 'O ditado precisa de internet. Escreve os artigos aqui em baixo.'.tr;
  return 'Não foi possível ouvir. Toca no microfone para tentar outra vez, ou escreve os artigos.'.tr;
}

/// "leite, pão e duas dúzias de ovos" → ["leite", "pão", "24 ovos"]. Separa por vírgulas, "e" e "mais"; os números
/// ditos por extenso no início passam a algarismos ("dois iogurtes" → "2 iogurtes") e "um"/"uma" desaparece.
/// Em inglês é igual: "milk, bread and two yoghurts" → ["milk", "bread", "2 yoghurts"].
List<String> parseSpokenItems(String text) => [
  for (final part in text.split(
    RegExp(r'\s*(?<!\d)[,;.](?!\d)\s*|\s+(?:e|mais|também|and|plus|also)\s+', caseSensitive: false),
  ))
    if (_spokenQuantity(part.trim()) case final item when item.isNotEmpty) item,
];

const _dozens = {
  'meia dúzia de': '6',
  'uma dúzia de': '12',
  'duas dúzias de': '24',
  'half a dozen': '6',
  'a dozen': '12',
  'two dozen': '24',
};
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
  'two': '2',
  'three': '3',
  'four': '4',
  'five': '5',
  'six': '6',
  'seven': '7',
  'eight': '8',
  'nine': '9',
  'ten': '10',
  'twelve': '12',
};

/// "um leite", "a milk": o artigo não conta como quantidade.
const _articles = {'um', 'uma', 'a', 'an', 'one'};

String _spokenQuantity(String part) {
  final lower = part.toLowerCase();
  for (final MapEntry(:key, :value) in _dozens.entries) {
    if (lower.startsWith('$key ')) return '$value ${part.substring(key.length + 1).trim()}';
  }
  final space = part.indexOf(' ');
  if (space < 0) return part;
  final first = lower.substring(0, space);
  final rest = part.substring(space + 1).trim();
  if (_articles.contains(first)) return rest;
  final number = _numbers[first];
  return number == null ? part : '$number $rest';
}
