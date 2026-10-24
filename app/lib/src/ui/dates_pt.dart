/// Nomes de dias e meses e formatos de data, usados pelo calendário e pelas listas: em português de Portugal ou
/// em inglês (britânico, com o dia antes do mês), conforme a língua da app.
///
/// São fixos (não dependem das localizações do Material) para os títulos e a grelha do calendário
/// terem sempre o mesmo formato, também nos testes.
library;

import '../l10n/strings.dart';

const _monthsPt = [
  'janeiro',
  'fevereiro',
  'março',
  'abril',
  'maio',
  'junho',
  'julho',
  'agosto',
  'setembro',
  'outubro',
  'novembro',
  'dezembro',
];

const _monthsEn = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

const _daysPt = ['segunda-feira', 'terça-feira', 'quarta-feira', 'quinta-feira', 'sexta-feira', 'sábado', 'domingo'];
const _daysEn = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

/// Abreviaturas dos dias da semana, de segunda ([DateTime.monday]) a domingo.
List<String> get weekdayShortNames => AppLanguage.isEnglish
    ? const ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
    : const ['seg', 'ter', 'qua', 'qui', 'sex', 'sáb', 'dom'];

String monthName(int month) => (AppLanguage.isEnglish ? _monthsEn : _monthsPt)[month - 1];

String _twoDigits(int n) => n.toString().padLeft(2, '0');

/// "08:05"
String formatTime(DateTime t) => '${_twoDigits(t.hour)}:${_twoDigits(t.minute)}';

/// "20 de outubro" / "20 October"
String formatDayMonth(DateTime d) =>
    AppLanguage.isEnglish ? '${d.day} ${monthName(d.month)}' : '${d.day} de ${monthName(d.month)}';

/// "Terça-feira, 20 de outubro" / "Tuesday, 20 October"
String formatLongDate(DateTime d) =>
    capitalize('${(AppLanguage.isEnglish ? _daysEn : _daysPt)[d.weekday - 1]}, ${formatDayMonth(d)}');

/// "20/10/2026"
String formatShortDate(DateTime d) => '${_twoDigits(d.day)}/${_twoDigits(d.month)}/${d.year}';

/// "20/10"
String formatDayMonthShort(DateTime d) => '${_twoDigits(d.day)}/${_twoDigits(d.month)}';

String capitalize(String text) => text.isEmpty ? text : '${text[0].toUpperCase()}${text.substring(1)}';
