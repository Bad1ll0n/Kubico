import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import '../session/session_controller.dart';
import 'auth_screen.dart';
import 'home_screen.dart';
import 'onboarding_screen.dart';
import 'theme.dart';

/// A app fala português de Portugal: datas, horas (24 h) e semanas a começar à segunda-feira.
const kubicoLocale = Locale('pt', 'PT');

class KubicoApp extends StatelessWidget {
  const KubicoApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Kubico',
      debugShowCheckedModeBanner: false,
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      locale: kubicoLocale,
      supportedLocales: const [kubicoLocale],
      localizationsDelegates: GlobalMaterialLocalizations.delegates,
      home: const _Root(),
    );
  }
}

/// Decide o ecrã inicial a partir do estado da sessão.
class _Root extends StatelessWidget {
  const _Root();

  @override
  Widget build(BuildContext context) {
    final session = context.watch<SessionController>();
    return switch (session.status) {
      SessionStatus.loading => const Scaffold(body: Center(child: CircularProgressIndicator())),
      SessionStatus.signedOut => const AuthScreen(),
      SessionStatus.signedIn when session.households.isEmpty => const OnboardingScreen(),
      SessionStatus.signedIn => const HomeScreen(),
    };
  }
}
