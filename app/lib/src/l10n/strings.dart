/// A língua da app (português de Portugal ou inglês).
///
/// Os textos escrevem-se em português no código e `.tr` devolve-os na língua escolhida. A tradução para inglês
/// está em `strings_en.dart`, com a mesma frase portuguesa como chave; nas frases com partes variáveis
/// (`'Olá, $nome!'.tr`), a chave tem `{0}`, `{1}`… no lugar delas e o que lá estiver passa igual para a tradução.
library;

import 'package:flutter/widgets.dart';

import '../storage/key_value_store.dart';
import 'strings_en.dart';

/// As línguas da app e a que está escolhida.
abstract final class AppLanguage {
  static const portuguese = 'pt';
  static const english = 'en';
  static const all = [portuguese, english];

  /// O nome de cada língua, escrito nessa língua (assim encontra-se sempre).
  static const names = {portuguese: 'Português', english: 'English'};

  static const _storageKey = 'kubico.language';

  /// A língua atual. A app ouve-a e desenha-se outra vez quando muda.
  static final current = ValueNotifier<String>(portuguese);

  static String get code => current.value;

  static bool get isEnglish => current.value == english;

  /// Para as datas e horas do Material (calendários, relógios) e a primeira linha do cabeçalho Accept-Language.
  static Locale get locale => isEnglish ? const Locale('en', 'GB') : const Locale('pt', 'PT');

  static String get acceptLanguage => isEnglish ? 'en-GB,en;q=0.9' : 'pt-PT,pt;q=0.9';

  static KeyValueStore? _store;

  /// "en", "en-GB"… é inglês; tudo o resto é português.
  static String normalize(String? code) =>
      code != null && code.toLowerCase().startsWith(english) ? english : portuguese;

  /// Lê a língua guardada no dispositivo (português se nunca se escolheu).
  static Future<void> restore(KeyValueStore store) async {
    _store = store;
    try {
      current.value = normalize(await store.read(_storageKey));
    } catch (_) {
      // Sem acesso ao armazenamento: fica em português.
    }
  }

  /// Muda a língua e guarda-a no dispositivo.
  static Future<void> set(String code) async {
    current.value = normalize(code);
    _translated.clear();
    try {
      await _store?.write(_storageKey, current.value);
    } catch (_) {
      // Não ficou guardada: no próximo arranque volta à anterior.
    }
  }
}

extension Translate on String {
  /// Este texto (escrito em português) na língua da app.
  String get tr => AppLanguage.isEnglish ? _english(this) : this;
}

/// Traduções já feitas das frases com partes variáveis (para não repetir a procura em cada desenho).
final _translated = <String, String>{};

String _english(String text) {
  final exact = englishTexts[text];
  if (exact != null) return exact;
  final cached = _translated[text];
  if (cached != null) return cached;

  var result = text;
  for (final pattern in _patterns) {
    final match = pattern.regex.firstMatch(text);
    if (match != null) {
      result = pattern.english.replaceAllMapped(_placeholder, (m) => match.group(int.parse(m[1]!) + 1) ?? '');
      break;
    }
  }
  if (_translated.length > 2000) _translated.clear();
  return _translated[text] = result;
}

final _placeholder = RegExp(r'\{(\d+)\}');

typedef _Pattern = ({RegExp regex, String english});

/// As frases com partes variáveis, das mais específicas (mais texto fixo) para as mais gerais.
final List<_Pattern> _patterns = () {
  final patterns = <(int, _Pattern)>[];
  for (final MapEntry(:key, :value) in englishTexts.entries) {
    final placeholders = _placeholder.allMatches(key).toList();
    if (placeholders.isEmpty) continue;
    // Cada {n} vira um grupo; os grupos ficam pela ordem de n para a tradução os ir buscar pelo número.
    final order = [for (final p in placeholders) int.parse(p[1]!)];
    final buffer = StringBuffer('^');
    var last = 0;
    var fixed = 0;
    for (final p in placeholders) {
      buffer.write(RegExp.escape(key.substring(last, p.start)));
      fixed += p.start - last;
      buffer.write('(.+?)');
      last = p.end;
    }
    buffer.write(RegExp.escape(key.substring(last)));
    fixed += key.length - last;
    buffer.write(r'$');
    // Os grupos contam-se por posição: na tradução, cada {n} passa a ser a posição desse {n} na chave.
    final english = value.replaceAllMapped(_placeholder, (m) => '{${order.indexOf(int.parse(m[1]!))}}');
    patterns.add((fixed, (regex: RegExp(buffer.toString(), dotAll: true), english: english)));
  }
  patterns.sort((a, b) => b.$1.compareTo(a.$1));
  return [for (final (_, p) in patterns) p];
}();

@visibleForTesting
List<String> get translationPatterns => [for (final p in _patterns) p.regex.pattern];
