import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'src/api/api_client.dart';
import 'src/config.dart';
import 'src/realtime/household_realtime.dart';
import 'src/session/session_controller.dart';
import 'src/session/token_store.dart';
import 'src/shopping/shopping_repository.dart';
import 'src/storage/key_value_store.dart';
import 'src/ui/app.dart';

void main() {
  final api = ApiClient(baseUrl: AppConfig.apiUrl);
  final session = SessionController(api: api, store: const SecureTokenStore())..restore();
  final realtime = HouseholdRealtime(baseUrl: AppConfig.apiUrl, accessToken: session.freshAccessToken);
  final shopping = ShoppingRepository(api: api, store: PreferencesStore())..restore();

  runApp(
    MultiProvider(
      providers: [
        Provider.value(value: api),
        ChangeNotifierProvider.value(value: session),
        Provider.value(value: realtime),
        ChangeNotifierProvider.value(value: shopping),
      ],
      child: const KubicoApp(),
    ),
  );
}
