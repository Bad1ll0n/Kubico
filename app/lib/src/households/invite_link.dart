import '../config.dart';

/// O link de um convite: abre a app web com o código já preenchido. Null quando não se sabe onde está a app web
/// (numa app nativa sem --dart-define=WEB_URL); aí partilha-se só o código.
String? inviteLink(String code, {String? webUrl}) {
  final base = webUrl ?? AppConfig.webUrl;
  if (base == null || base.isEmpty) return null;
  return '${base.replaceAll(RegExp(r'/+$'), '')}/?convite=$code';
}

/// O código de um convite, escrito à mão ou lido de um QR: o próprio código (com ou sem espaços e hífenes) ou um
/// link com `?convite=`. Null se o texto não for nada disto.
String? inviteCodeFrom(String text) {
  final trimmed = text.trim();
  final uri = Uri.tryParse(trimmed);
  final fromLink = uri != null && uri.hasScheme ? uri.queryParameters['convite'] : null;
  if (uri != null && uri.hasScheme && fromLink == null) return null;

  final candidate = (fromLink ?? trimmed).toUpperCase().replaceAll(RegExp(r'[\s-]'), '');
  return RegExp(r'^[A-Z0-9]{6,12}$').hasMatch(candidate) ? candidate : null;
}
