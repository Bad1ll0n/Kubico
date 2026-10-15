import 'package:shared_preferences/shared_preferences.dart';

/// Texto guardado no dispositivo entre arranques (cópia das listas, alterações por enviar).
abstract class KeyValueStore {
  Future<String?> read(String key);
  Future<void> write(String key, String? value);
}

/// Preferências do sistema (no browser, o localStorage). Não é para segredos: esses vão para o TokenStore.
class PreferencesStore implements KeyValueStore {
  PreferencesStore() : _prefs = SharedPreferencesAsync();

  final SharedPreferencesAsync _prefs;

  @override
  Future<String?> read(String key) => _prefs.getString(key);

  @override
  Future<void> write(String key, String? value) => value == null ? _prefs.remove(key) : _prefs.setString(key, value);
}

/// Usado nos testes.
class MemoryKeyValueStore implements KeyValueStore {
  final Map<String, String> values = {};

  @override
  Future<String?> read(String key) async => values[key];

  @override
  Future<void> write(String key, String? value) async => value == null ? values.remove(key) : values[key] = value;
}
