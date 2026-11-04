// Os dados de datas (_dateSymbols, _datePatterns) são as entradas 'pt_PT' e 'en_GB' copiadas tal e qual de
// flutter_localizations/lib/src/l10n/generated_date_localizations.dart; ao mudar de versão do Flutter, copiam-se outra vez.

import 'package:flutter/cupertino.dart' show CupertinoLocalizations;
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/date_symbol_data_custom.dart' as date_symbol_data_custom;
import 'package:intl/date_symbols.dart' as intl;
import 'package:intl/intl.dart' as intl;

/// Os delegados de localização da app, só com português de Portugal e inglês (P8). Os
/// [GlobalMaterialLocalizations.delegates] trazem as ~80 línguas do Material e do Cupertino e os dados de datas de
/// todas elas (~380 KB no main.dart.js); estes instanciam diretamente as classes de pt_PT e en_GB, e o dart2js retira o
/// resto. Qualquer variante de "pt" usa pt_PT e qualquer variante de "en" usa en_GB.
const List<LocalizationsDelegate<dynamic>> kubicoLocalizationsDelegates = [
  _MaterialDelegate(),
  _CupertinoDelegate(),
  _WidgetsDelegate(),
];

bool _supported(Locale locale) => locale.languageCode == 'pt' || locale.languageCode == 'en';

String _localeName(Locale locale) => locale.languageCode == 'pt' ? 'pt_PT' : 'en_GB';

var _dateDataLoaded = false;

/// Os dados de datas (nomes dos meses e dos dias, formatos) das duas línguas, uma só vez.
void _loadDateData() {
  if (_dateDataLoaded) return;
  for (final locale in ['pt_PT', 'en_GB']) {
    date_symbol_data_custom.initializeDateFormattingCustom(
      locale: locale,
      symbols: _dateSymbols(locale),
      patterns: _datePatterns[locale],
    );
  }
  _dateDataLoaded = true;
}

class _MaterialDelegate extends LocalizationsDelegate<MaterialLocalizations> {
  const _MaterialDelegate();

  @override
  bool isSupported(Locale locale) => _supported(locale);

  @override
  Future<MaterialLocalizations> load(Locale locale) {
    _loadDateData();
    final name = _localeName(locale);
    final fullYearFormat = intl.DateFormat.y(name);
    final compactDateFormat = intl.DateFormat.yMd(name);
    final shortDateFormat = intl.DateFormat.yMMMd(name);
    final mediumDateFormat = intl.DateFormat.MMMEd(name);
    final longDateFormat = intl.DateFormat.yMMMMEEEEd(name);
    final yearMonthFormat = intl.DateFormat.yMMMM(name);
    final shortMonthDayFormat = intl.DateFormat.MMMd(name);
    final decimalFormat = intl.NumberFormat.decimalPattern(name);
    final twoDigitZeroPaddedFormat = intl.NumberFormat('00', name);
    return SynchronousFuture<MaterialLocalizations>(
      name == 'pt_PT'
          ? MaterialLocalizationPtPt(
              fullYearFormat: fullYearFormat,
              compactDateFormat: compactDateFormat,
              shortDateFormat: shortDateFormat,
              mediumDateFormat: mediumDateFormat,
              longDateFormat: longDateFormat,
              yearMonthFormat: yearMonthFormat,
              shortMonthDayFormat: shortMonthDayFormat,
              decimalFormat: decimalFormat,
              twoDigitZeroPaddedFormat: twoDigitZeroPaddedFormat,
            )
          : MaterialLocalizationEnGb(
              fullYearFormat: fullYearFormat,
              compactDateFormat: compactDateFormat,
              shortDateFormat: shortDateFormat,
              mediumDateFormat: mediumDateFormat,
              longDateFormat: longDateFormat,
              yearMonthFormat: yearMonthFormat,
              shortMonthDayFormat: shortMonthDayFormat,
              decimalFormat: decimalFormat,
              twoDigitZeroPaddedFormat: twoDigitZeroPaddedFormat,
            ),
    );
  }

  @override
  bool shouldReload(_MaterialDelegate old) => false;
}

class _CupertinoDelegate extends LocalizationsDelegate<CupertinoLocalizations> {
  const _CupertinoDelegate();

  @override
  bool isSupported(Locale locale) => _supported(locale);

  @override
  Future<CupertinoLocalizations> load(Locale locale) {
    _loadDateData();
    final name = _localeName(locale);
    final fullYearFormat = intl.DateFormat.y(name);
    final dayFormat = intl.DateFormat.d(name);
    final weekdayFormat = intl.DateFormat.E(name);
    final mediumDateFormat = intl.DateFormat.MMMEd(name);
    final singleDigitHourFormat = intl.DateFormat('HH', name);
    final singleDigitMinuteFormat = intl.DateFormat.m(name);
    final doubleDigitMinuteFormat = intl.DateFormat('mm', name);
    final singleDigitSecondFormat = intl.DateFormat.s(name);
    final decimalFormat = intl.NumberFormat.decimalPattern(name);
    return SynchronousFuture<CupertinoLocalizations>(
      name == 'pt_PT'
          ? CupertinoLocalizationPtPt(
              fullYearFormat: fullYearFormat,
              dayFormat: dayFormat,
              weekdayFormat: weekdayFormat,
              mediumDateFormat: mediumDateFormat,
              singleDigitHourFormat: singleDigitHourFormat,
              singleDigitMinuteFormat: singleDigitMinuteFormat,
              doubleDigitMinuteFormat: doubleDigitMinuteFormat,
              singleDigitSecondFormat: singleDigitSecondFormat,
              decimalFormat: decimalFormat,
            )
          : CupertinoLocalizationEnGb(
              fullYearFormat: fullYearFormat,
              dayFormat: dayFormat,
              weekdayFormat: weekdayFormat,
              mediumDateFormat: mediumDateFormat,
              singleDigitHourFormat: singleDigitHourFormat,
              singleDigitMinuteFormat: singleDigitMinuteFormat,
              doubleDigitMinuteFormat: doubleDigitMinuteFormat,
              singleDigitSecondFormat: singleDigitSecondFormat,
              decimalFormat: decimalFormat,
            ),
    );
  }

  @override
  bool shouldReload(_CupertinoDelegate old) => false;
}

class _WidgetsDelegate extends LocalizationsDelegate<WidgetsLocalizations> {
  const _WidgetsDelegate();

  @override
  bool isSupported(Locale locale) => _supported(locale);

  @override
  Future<WidgetsLocalizations> load(Locale locale) => SynchronousFuture<WidgetsLocalizations>(
    _localeName(locale) == 'pt_PT' ? const WidgetsLocalizationPtPt() : const WidgetsLocalizationEnGb(),
  );

  @override
  bool shouldReload(_WidgetsDelegate old) => false;
}

intl.DateSymbols _dateSymbols(String locale) => switch (locale) {
  'pt_PT' => intl.DateSymbols(
    NAME: 'pt_PT',
    ERAS: const <String>['a.C.', 'd.C.'],
    ERANAMES: const <String>['antes de Cristo', 'depois de Cristo'],
    NARROWMONTHS: const <String>['J', 'F', 'M', 'A', 'M', 'J', 'J', 'A', 'S', 'O', 'N', 'D'],
    STANDALONENARROWMONTHS: const <String>['J', 'F', 'M', 'A', 'M', 'J', 'J', 'A', 'S', 'O', 'N', 'D'],
    MONTHS: const <String>[
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
    ],
    STANDALONEMONTHS: const <String>[
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
    ],
    SHORTMONTHS: const <String>[
      'jan.',
      'fev.',
      'mar.',
      'abr.',
      'mai.',
      'jun.',
      'jul.',
      'ago.',
      'set.',
      'out.',
      'nov.',
      'dez.',
    ],
    STANDALONESHORTMONTHS: const <String>[
      'jan.',
      'fev.',
      'mar.',
      'abr.',
      'mai.',
      'jun.',
      'jul.',
      'ago.',
      'set.',
      'out.',
      'nov.',
      'dez.',
    ],
    WEEKDAYS: const <String>[
      'domingo',
      'segunda-feira',
      'terça-feira',
      'quarta-feira',
      'quinta-feira',
      'sexta-feira',
      'sábado',
    ],
    STANDALONEWEEKDAYS: const <String>[
      'domingo',
      'segunda-feira',
      'terça-feira',
      'quarta-feira',
      'quinta-feira',
      'sexta-feira',
      'sábado',
    ],
    SHORTWEEKDAYS: const <String>['domingo', 'segunda', 'terça', 'quarta', 'quinta', 'sexta', 'sábado'],
    STANDALONESHORTWEEKDAYS: const <String>['domingo', 'segunda', 'terça', 'quarta', 'quinta', 'sexta', 'sábado'],
    NARROWWEEKDAYS: const <String>['D', 'S', 'T', 'Q', 'Q', 'S', 'S'],
    STANDALONENARROWWEEKDAYS: const <String>['D', 'S', 'T', 'Q', 'Q', 'S', 'S'],
    SHORTQUARTERS: const <String>['T1', 'T2', 'T3', 'T4'],
    QUARTERS: const <String>['1.º trimestre', '2.º trimestre', '3.º trimestre', '4.º trimestre'],
    AMPMS: const <String>['a.m.', 'p.m.'],
    DATEFORMATS: const <String>["EEEE, d 'de' MMMM 'de' y", "d 'de' MMMM 'de' y", 'dd/MM/y', 'dd/MM/yy'],
    TIMEFORMATS: const <String>['HH:mm:ss zzzz', 'HH:mm:ss z', 'HH:mm:ss', 'HH:mm'],
    FIRSTDAYOFWEEK: 6,
    WEEKENDRANGE: const <int>[5, 6],
    FIRSTWEEKCUTOFFDAY: 2,
    DATETIMEFORMATS: const <String>['{1}, {0}', '{1}, {0}', '{1}, {0}', '{1}, {0}'],
  ),
  _ => intl.DateSymbols(
    NAME: 'en_GB',
    ERAS: const <String>['BC', 'AD'],
    ERANAMES: const <String>['Before Christ', 'Anno Domini'],
    NARROWMONTHS: const <String>['J', 'F', 'M', 'A', 'M', 'J', 'J', 'A', 'S', 'O', 'N', 'D'],
    STANDALONENARROWMONTHS: const <String>['J', 'F', 'M', 'A', 'M', 'J', 'J', 'A', 'S', 'O', 'N', 'D'],
    MONTHS: const <String>[
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
    ],
    STANDALONEMONTHS: const <String>[
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
    ],
    SHORTMONTHS: const <String>['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sept', 'Oct', 'Nov', 'Dec'],
    STANDALONESHORTMONTHS: const <String>[
      'Jan',
      'Feb',
      'Mar',
      'Apr',
      'May',
      'Jun',
      'Jul',
      'Aug',
      'Sept',
      'Oct',
      'Nov',
      'Dec',
    ],
    WEEKDAYS: const <String>['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
    STANDALONEWEEKDAYS: const <String>['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
    SHORTWEEKDAYS: const <String>['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
    STANDALONESHORTWEEKDAYS: const <String>['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
    NARROWWEEKDAYS: const <String>['S', 'M', 'T', 'W', 'T', 'F', 'S'],
    STANDALONENARROWWEEKDAYS: const <String>['S', 'M', 'T', 'W', 'T', 'F', 'S'],
    SHORTQUARTERS: const <String>['Q1', 'Q2', 'Q3', 'Q4'],
    QUARTERS: const <String>['1st quarter', '2nd quarter', '3rd quarter', '4th quarter'],
    AMPMS: const <String>['am', 'pm'],
    DATEFORMATS: const <String>['EEEE, d MMMM y', 'd MMMM y', 'd MMM y', 'dd/MM/y'],
    TIMEFORMATS: const <String>['HH:mm:ss zzzz', 'HH:mm:ss z', 'HH:mm:ss', 'HH:mm'],
    FIRSTDAYOFWEEK: 0,
    WEEKENDRANGE: const <int>[5, 6],
    FIRSTWEEKCUTOFFDAY: 3,
    DATETIMEFORMATS: const <String>['{1}, {0}', '{1}, {0}', '{1}, {0}', '{1}, {0}'],
  ),
};

const Map<String, Map<String, String>> _datePatterns = {
  'pt_PT': <String, String>{
    'd': 'd',
    'E': 'ccc',
    'EEEE': 'cccc',
    'LLL': 'LLL',
    'LLLL': 'LLLL',
    'M': 'L',
    'Md': 'dd/MM',
    'MEd': 'EEE, dd/MM',
    'MMM': 'LLL',
    'MMMd': 'd/MM',
    'MMMEd': 'EEE, d/MM',
    'MMMM': 'LLLL',
    'MMMMd': "d 'de' MMMM",
    'MMMMEEEEd': "cccc, d 'de' MMMM",
    'QQQ': 'QQQ',
    'QQQQ': 'QQQQ',
    'y': 'y',
    'yM': 'MM/y',
    'yMd': 'dd/MM/y',
    'yMEd': 'EEE, dd/MM/y',
    'yMMM': 'MM/y',
    'yMMMd': 'd/MM/y',
    'yMMMEd': 'EEE, d/MM/y',
    'yMMMM': "MMMM 'de' y",
    'yMMMMd': "d 'de' MMMM 'de' y",
    'yMMMMEEEEd': "EEEE, d 'de' MMMM 'de' y",
    'yQQQ': "QQQQ 'de' y",
    'yQQQQ': "QQQQ 'de' y",
    'H': 'HH',
    'Hm': 'HH:mm',
    'Hms': 'HH:mm:ss',
    'j': 'HH',
    'jm': 'HH:mm',
    'jms': 'HH:mm:ss',
    'jmv': 'HH:mm v',
    'jmz': 'HH:mm z',
    'jz': 'HH z',
    'm': 'm',
    'ms': 'mm:ss',
    's': 's',
    'v': 'v',
    'z': 'z',
    'zzzz': 'zzzz',
    'ZZZZ': 'ZZZZ',
  },
  'en_GB': <String, String>{
    'd': 'd',
    'E': 'ccc',
    'EEEE': 'cccc',
    'LLL': 'LLL',
    'LLLL': 'LLLL',
    'M': 'L',
    'Md': 'dd/MM',
    'MEd': 'EEE dd/MM',
    'MMM': 'LLL',
    'MMMd': 'd MMM',
    'MMMEd': 'EEE d MMM',
    'MMMM': 'LLLL',
    'MMMMd': 'd MMMM',
    'MMMMEEEEd': 'EEEE d MMMM',
    'QQQ': 'QQQ',
    'QQQQ': 'QQQQ',
    'y': 'y',
    'yM': 'MM/y',
    'yMd': 'dd/MM/y',
    'yMEd': 'EEE, dd/MM/y',
    'yMMM': 'MMM y',
    'yMMMd': 'd MMM y',
    'yMMMEd': 'EEE, d MMM y',
    'yMMMM': 'MMMM y',
    'yMMMMd': 'd MMMM y',
    'yMMMMEEEEd': 'EEEE, d MMMM y',
    'yQQQ': 'QQQ y',
    'yQQQQ': 'QQQQ y',
    'H': 'HH',
    'Hm': 'HH:mm',
    'Hms': 'HH:mm:ss',
    'j': 'HH',
    'jm': 'HH:mm',
    'jms': 'HH:mm:ss',
    'jmv': 'HH:mm v',
    'jmz': 'HH:mm z',
    'jz': 'HH z',
    'm': 'm',
    'ms': 'mm:ss',
    's': 's',
    'v': 'v',
    'z': 'z',
    'zzzz': 'zzzz',
    'ZZZZ': 'ZZZZ',
  },
};
