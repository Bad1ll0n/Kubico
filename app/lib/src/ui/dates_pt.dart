/// Nomes de dias e meses e formatos de data em português de Portugal, usados pelo calendário.
///
/// São fixos (não dependem das localizações do Material) para os títulos e a grelha do calendário
/// terem sempre o mesmo formato, também nos testes.
library;

const _months = [
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

const _days = ['segunda-feira', 'terça-feira', 'quarta-feira', 'quinta-feira', 'sexta-feira', 'sábado', 'domingo'];

/// Abreviaturas dos dias da semana, de segunda ([DateTime.monday]) a domingo.
const weekdayShortNames = ['seg', 'ter', 'qua', 'qui', 'sex', 'sáb', 'dom'];

String monthName(int month) => _months[month - 1];

String _twoDigits(int n) => n.toString().padLeft(2, '0');

/// "08:05"
String formatTime(DateTime t) => '${_twoDigits(t.hour)}:${_twoDigits(t.minute)}';

/// "20 de outubro"
String formatDayMonth(DateTime d) => '${d.day} de ${monthName(d.month)}';

/// "Terça-feira, 20 de outubro"
String formatLongDate(DateTime d) => capitalize('${_days[d.weekday - 1]}, ${formatDayMonth(d)}');

/// "20/10/2026"
String formatShortDate(DateTime d) => '${_twoDigits(d.day)}/${_twoDigits(d.month)}/${d.year}';

String capitalize(String text) => text.isEmpty ? text : '${text[0].toUpperCase()}${text.substring(1)}';
