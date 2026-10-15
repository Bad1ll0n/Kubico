import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// Como mostrar cada tipo de dispositivo (vem da app, no cabeçalho X-Kubico-Device).
String deviceLabel(String? kind) => switch (kind) {
  'android' => 'Telemóvel ou tablet Android',
  'ios' => 'iPhone ou iPad',
  'windows' => 'Computador Windows',
  'macos' => 'Mac',
  'linux' => 'Computador Linux',
  'web' => 'Browser',
  _ => 'Dispositivo desconhecido',
};

IconData _deviceIcon(String? kind) => switch (kind) {
  'android' || 'ios' => Icons.smartphone,
  'windows' || 'macos' || 'linux' => Icons.computer,
  'web' => Icons.language,
  _ => Icons.devices_other,
};

/// Os dispositivos onde há sessão iniciada, para terminar a de um telemóvel perdido ou de um computador emprestado.
class DevicesScreen extends StatefulWidget {
  const DevicesScreen({super.key});

  @override
  State<DevicesScreen> createState() => _DevicesScreenState();
}

class _DevicesScreenState extends State<DevicesScreen> {
  List<DeviceSession>? _sessions;
  Object? _error;

  ApiClient get _api => context.read<ApiClient>();

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final sessions = await _api.getSessions();
      if (mounted) {
        setState(() {
          _sessions = sessions;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  Future<void> _end(DeviceSession session) async {
    try {
      await _api.endSession(session.id);
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Sessão terminada: ${deviceLabel(session.device).toLowerCase()}.')));
      await _load();
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final sessions = _sessions;
    return Scaffold(
      appBar: AppBar(title: const Text('Dispositivos com sessão iniciada')),
      body: sessions == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error.toString()))
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 24),
                children: [
                  for (final s in sessions)
                    Card(
                      child: ListTile(
                        leading: Icon(_deviceIcon(s.device)),
                        title: Text(deviceLabel(s.device)),
                        subtitle: Text(
                          'Usado a ${formatShortDate(s.lastActiveAt)} às ${formatTime(s.lastActiveAt)}'
                          ' · desde ${formatShortDate(s.startedAt)}',
                        ),
                        trailing: s.isCurrent
                            ? Text('Este dispositivo', style: TextStyle(color: theme.colorScheme.primary))
                            : TextButton(onPressed: () => _end(s), child: const Text('Terminar')),
                      ),
                    ),
                  Padding(
                    padding: const EdgeInsets.all(16),
                    child: Text(
                      'Terminar a sessão num dispositivo obriga a entrar outra vez nesse dispositivo. Para sair de '
                      'todos de uma vez, usa «Terminar sessão em todos os dispositivos» no menu.',
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                ],
              ),
            ),
    );
  }
}
