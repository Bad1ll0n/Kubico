import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../session/session_controller.dart';
import 'dates_pt.dart';
import 'theme.dart';

/// Perguntas frequentes e, para o que lá não estiver, "Reportar um problema" ou "Sugerir uma ideia".
const helpTopics = [
  (
    'Como convido alguém para a casa?',
    'Abre os membros (o ícone das pessoas, no topo) e carrega em Convidar. Escolhe o papel e partilha o código ou o '
        'link. Quem recebe escolhe "Entrar com um convite" e escreve o código.',
  ),
  (
    'As crianças precisam de email?',
    'Não. Nos membros, carrega em "Criar perfil de criança" e escolhe um PIN. No telemóvel ou tablet da criança, '
        'escolhe "Entrar com perfil de criança", escreve o código da família, o nome e o PIN.',
  ),
  (
    'O que acontece quando alguém vai de férias?',
    'Marca a ausência no menu ao lado do nome. Enquanto estiver fora, as rotações saltam essa pessoa e as tarefas '
        'que lhe cabiam passam a quem vem a seguir.',
  ),
  (
    'A lista de compras funciona sem rede?',
    'Sim. As alterações ficam guardadas no telemóvel e são enviadas quando a rede voltar, sem repetir artigos.',
  ),
  (
    'Como funcionam os pontos e as recompensas?',
    'Cada tarefa feita dá os pontos que tiver. Em Recompensas, os pontos trocam-se pelos prémios que os adultos '
        'definirem; os pedidos das crianças esperam pela aprovação de um adulto.',
  ),
  (
    'Posso ver o calendário da casa no Google ou no Outlook?',
    'Sim. No Calendário, carrega em "Ver noutros calendários": podes ligar a tua conta Google ou Microsoft (o que '
        'mudares lá também muda aqui) ou subscrever um endereço só para ver.',
  ),
  (
    'Como recebo avisos no telemóvel?',
    'A app pede autorização para notificações quando entras. No sino, em Preferências, escolhes se queres os avisos '
        'logo, num resumo diário ou só na app, e as horas de silêncio.',
  ),
  (
    'Como saio de uma casa?',
    'Nos membros, carrega em "Sair desta casa". Se fores o único administrador, passa primeiro a administração a '
        'outra pessoa, no menu ao lado do nome dela.',
  ),
  (
    'Perdi o telemóvel. Como termino a sessão nele?',
    'No menu ⋮, abre "Dispositivos com sessão iniciada" e termina a sessão desse aparelho (ou em todos).',
  ),
];

class HelpScreen extends StatelessWidget {
  const HelpScreen({super.key, this.householdId});

  /// A casa aberta (vai com a mensagem, para quem a lê perceber o contexto).
  final String? householdId;

  Future<void> _send(BuildContext context, FeedbackKind kind) async {
    final message = await showDialog<String>(
      context: context,
      builder: (_) => _FeedbackDialog(kind: kind),
    );
    if (message == null || !context.mounted) return;
    try {
      await context.read<ApiClient>().sendFeedback(kind, message, householdId: householdId);
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Obrigado! A mensagem foi enviada.')));
      }
    } catch (e) {
      if (context.mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isSupport = context.watch<SessionController>().isSupport;
    return Scaffold(
      appBar: AppBar(title: const Text('Ajuda e sugestões')),
      body: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          Text('Perguntas frequentes', style: theme.textTheme.titleMedium),
          const SizedBox(height: 8),
          Card(
            child: Column(
              children: [
                for (final (question, answer) in helpTopics)
                  ExpansionTile(
                    title: Text(question),
                    childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                    expandedCrossAxisAlignment: CrossAxisAlignment.start,
                    children: [Text(answer)],
                  ),
              ],
            ),
          ),
          const SizedBox(height: 16),
          Text('Não encontraste o que procuravas?', style: theme.textTheme.titleMedium),
          const SizedBox(height: 8),
          Card(
            child: Column(
              children: [
                ListTile(
                  leading: const Icon(Icons.bug_report_outlined),
                  title: const Text('Reportar um problema'),
                  subtitle: const Text('Alguma coisa não funciona como devia.'),
                  onTap: () => _send(context, FeedbackKind.problem),
                ),
                ListTile(
                  leading: const Icon(Icons.lightbulb_outline),
                  title: const Text('Sugerir uma ideia'),
                  subtitle: const Text('O que faria o Kubico mais útil para a tua casa.'),
                  onTap: () => _send(context, FeedbackKind.idea),
                ),
              ],
            ),
          ),
          if (isSupport) ...[
            const SizedBox(height: 16),
            Card(
              child: ListTile(
                leading: const Icon(Icons.inbox_outlined),
                title: const Text('Mensagens recebidas'),
                subtitle: const Text('Os problemas e as ideias que os utilizadores enviaram.'),
                onTap: () =>
                    Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const FeedbackInboxScreen())),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _FeedbackDialog extends StatefulWidget {
  const _FeedbackDialog({required this.kind});

  final FeedbackKind kind;

  @override
  State<_FeedbackDialog> createState() => _FeedbackDialogState();
}

class _FeedbackDialogState extends State<_FeedbackDialog> {
  final _form = GlobalKey<FormState>();
  final _message = TextEditingController();

  @override
  void dispose() {
    _message.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final problem = widget.kind == FeedbackKind.problem;
    return AlertDialog(
      title: Text(problem ? 'Reportar um problema' : 'Sugerir uma ideia'),
      content: Form(
        key: _form,
        child: SizedBox(
          width: 420,
          child: TextFormField(
            controller: _message,
            autofocus: true,
            minLines: 4,
            maxLines: 8,
            maxLength: 2000,
            decoration: InputDecoration(
              labelText: problem ? 'O que aconteceu?' : 'Qual é a ideia?',
              hintText: problem ? 'O que estavas a fazer e o que viste no ecrã.' : null,
              alignLabelWithHint: true,
            ),
            validator: (v) => (v == null || v.trim().length < 5) ? 'Escreve um pouco mais.' : null,
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancelar')),
        FilledButton(
          onPressed: () {
            if (_form.currentState!.validate()) Navigator.pop(context, _message.text.trim());
          },
          child: const Text('Enviar'),
        ),
      ],
    );
  }
}

/// As mensagens recebidas, para quem mantém o Kubico (as contas em Support:AdminEmails no servidor).
class FeedbackInboxScreen extends StatefulWidget {
  const FeedbackInboxScreen({super.key});

  @override
  State<FeedbackInboxScreen> createState() => _FeedbackInboxScreenState();
}

class _FeedbackInboxScreenState extends State<FeedbackInboxScreen> {
  List<FeedbackItem>? _items;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final items = await context.read<ApiClient>().getFeedback();
      if (mounted) setState(() => _items = items);
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final items = _items;
    return Scaffold(
      appBar: AppBar(title: const Text('Mensagens recebidas')),
      body: items == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error.toString()))
          : items.isEmpty
          ? const Center(child: Text('Ainda não chegou nenhuma mensagem.'))
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView.separated(
                padding: const EdgeInsets.all(12),
                itemCount: items.length,
                separatorBuilder: (_, _) => const SizedBox(height: 8),
                itemBuilder: (context, i) {
                  final item = items[i];
                  final who = [item.userName, item.device].whereType<String>().join(' · ');
                  return Card(
                    child: ListTile(
                      leading: Icon(
                        item.kind == FeedbackKind.problem ? Icons.bug_report_outlined : Icons.lightbulb_outline,
                      ),
                      title: Text(item.message),
                      subtitle: Text('$who · ${formatShortDate(item.createdAt)} ${formatTime(item.createdAt)}'),
                    ),
                  );
                },
              ),
            ),
    );
  }
}
