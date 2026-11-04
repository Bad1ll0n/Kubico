# Kubico — Fase 3: produto e UX

Revisor: produto / investigação de UX (apps de família e de consumo). Base: branch `mvp-continuacao` @ 60bb5a1, leitura do código da app (`app/lib/src/ui`, sessão, avisos, l10n), dos modelos e permissões do servidor e da especificação (Claude Docs, secção "User stories do MVP"). Não corri builds nem testes, e não avaliei o aspeto visual.

## Veredicto

O motor está muito à frente da experiência. Há permissões por área, desfazer, sincronização com o Google, o Outlook e o iCloud, compras sem rede e pontos sem corridas. Mas a app ainda se apresenta como um conjunto de **módulos para um administrador** e não como **o dia da família**. Quem chega cai em "Compras". A criança vê as tarefas de toda a casa. O avô recebe a app completa. Convidar a família obriga a copiar um código à mão. E quem se esquecer da palavra-passe perde a conta. Quanto à meta de "onboarding em menos de 2 minutos": **cumpre-se para o administrador sozinho** (cerca de 1 minuto: registo com 3 campos, nome da casa e modelo). **Não se cumpre para pôr a família lá dentro**, a métrica que conta (dois ou mais membros ativos): calculo 5 a 10 minutos por pessoa, contando com a instalação, porque não há partilha nativa nem link que abra a app.

## User stories do MVP (20)

| # | História | Estado | Nota |
|---|---|---|---|
| 1 | Criar agregado com nome | Cumprida | `onboarding_screen.dart:102-151` |
| 2 | Convidar por link/QR com papel | **Em parte** | O QR e o código existem. O link só existe com `WEB_URL` e abre a app web, não a nativa. Não há partilha nativa (UX-05) |
| 3 | Perfil de criança com PIN | Cumprida | Falta editar o nome e a data de nascimento na app e o consentimento parental (UX-04, UX-15) |
| 4 | Permissões por papel e área | Cumprida | `permissions_screen.dart` |
| 5 | Vários agregados e alternar | Cumprida | `home_screen.dart:375-412` |
| 6 | Eventos com a cor de cada pessoa | Cumprida | Só pela cor (acessibilidade, UX-09) |
| 7 | Ligar o Google ou o Outlook nos dois sentidos | Cumprida | Fácil de não encontrar (UX-17) |
| 8 | Quem leva e quem vai buscar | Cumprida | |
| 9 | Aviso de conflitos quando ninguém está disponível | **Em parte** | Só aparece no ecrã (snackbar ao gravar, ícone na linha). Não há aviso para quem vai levar nem a deteção de "ninguém disponível" para uma boleia |
| 10 | Tarefas recorrentes com rotação | Cumprida | Nenhum modelo traz rotação (UX-03) |
| 11 | Criança vê as **suas** tarefas do dia e marca-as | **Em parte** | Vê todas as tarefas da casa, sem filtro, e entra em Compras (UX-02) |
| 12 | Recompensas trocáveis por pontos | Cumprida | |
| 13 | Quem fez o quê no último mês | Cumprida | "Últimos 30 dias" e `ChoreHistoryScreen` |
| 14 | Artigos em tempo real | Cumprida | |
| 15 | Lista sem rede | Cumprida | Só as compras são offline (UX-13) |
| 16 | Voz e código de barras | Cumprida | |
| 17 | Ordenados por categoria ou corredor | **Em parte (quase em falta)** | Só os artigos lidos por código de barras têm categoria. Os escritos ou ditados não têm, e não se edita um artigo (UX-10) |
| 18 | Resumo diário | Cumprida | |
| 19 | Horas de silêncio | Cumprida | |
| 20 | Aviso quando me atribuem uma tarefa ou um evento | Cumprida | Sem push no iPhone (já planeado). Tocar no aviso não abre nada (UX-07) |

Ficam 15 cumpridas e 5 em parte. Das funcionalidades MVP da especificação **fora** das histórias, faltam as "sugestões pelo histórico" (compras). Dos requisitos transversais, faltam o modo tablet/kiosk, os widgets, o modo simples, os leitores de ecrã e o consentimento explícito (UX-04, UX-08, UX-09).

---

## Crítico

### UX-01 — Não há forma de recuperar nem de mudar a palavra-passe, nem de verificar o email
- **Onde:** `auth_screen.dart` (o formulário de entrada só tem "Entrar", "Criar uma conta nova" e "Entrar com perfil de criança", ~linhas 195-218). `account_screen.dart:103-160` (não há "mudar palavra-passe" nem "mudar nome"). No servidor, `AuthEndpoints`/`AccountEndpoints` não têm `forgot`/`reset`/`change-password`, e não há envio de email. A validação do email é só `contains('@')` (`auth_screen.dart:181`).
- **Cenário:** a mãe cria a casa num domingo e um mês depois troca de telemóvel. Não se lembra da palavra-passe e fica sem caminho: a casa, os perfis dos filhos e os pontos ficam presos, e o único remédio é pedir ao suporte. Outro caso: um email mal escrito no registo cria uma conta que nunca se recupera. Num grupo de testes com 10 a 20 famílias isto acontece na primeira semana.
- **Correção:** um "Esqueci-me da palavra-passe" com uma ligação de uso único por email (15 a 30 min, que termina as sessões ao repor), mudar a palavra-passe em "A minha conta", e verificar o email no registo (um código de 6 algarismos chega e não bloqueia a entrada). Isto obriga a escolher um fornecedor de email (SMTP ou um serviço transacional), o que convém decidir já. Em alternativa, entrar com Google ou Apple, que a especificação já prevê e que também encurta o onboarding.

### UX-02 — Não existe "o meu dia": a app abre em Compras para todos, também para as crianças
- **Onde:** `home_screen.dart:50` (`_section = HouseholdModule.shopping`) e `:40-45`. `chores_view.dart:307-370` mostra as tarefas de toda a casa, agrupadas por prazo, sem filtro por pessoa. Uma criança (Participate) vê as dos irmãos com o botão desativado ("Atribuída a outra pessoa").
- **Cenário:** o Tomás, de 8 anos, entra com o PIN e vê a lista do supermercado. Vai a Tarefas e encontra 12 linhas: as dele, as da irmã e as dos pais, com subtítulos como "manhã · 25/10 · diária · 0/3 passos". Não percebe o que é dele e desiste. A história 11 ("as minhas tarefas do dia") não se cumpre. Com o pai acontece o mesmo: a pergunta de cada manhã ("o que há hoje?") pede três separadores.
- **Correção:**
  1. Um separador **Hoje** como primeira área: eventos de hoje, as minhas tarefas de hoje e as em atraso, os pedidos à espera de aprovação (para quem gere) e a contagem da lista principal. Os dados já estão no servidor.
  2. Em Tarefas, um filtro "As minhas / Todas" que nas crianças começa em "As minhas".
  3. Guardar a última área aberta no dispositivo.

### UX-03 — O modelo "Família com crianças" gera rotinas partilhadas: a primeira criança a acabar gasta a dos irmãos
- **Onde:** `backend/src/Kubico.Api/Households/HouseholdTemplates.cs:56-63` e `Apply` (`:133-148`). Todas as tarefas nascem sem responsável e sem rotação, com `DueDate = today`. Cada ocorrência só se conclui uma vez (regra do domínio).
- **Cenário:** a família tem dois filhos. Às 7h40 a Rita marca os 3 passos da "Rotina da manhã" e ganha 2 pontos. O irmão abre a app e encontra a rotina "Já está feita. Volta a estar disponível a 26/10". Na primeira manhã de uso, o produto mostra que não serve para mais de uma criança. Além disso, as 3 tarefas semanais ficam "Hoje" no primeiro dia, e a primeira impressão é uma lista de 5 coisas por fazer de seguida.
- **Correção:**
  - No modelo, as rotinas passam a ser **por criança**. Ao criar o primeiro perfil de criança, a app pergunta "Criar as rotinas da manhã e da noite para a Rita?" e duplica-as com responsável.
  - No editor, uma opção "Uma para cada pessoa (cada um faz a sua)" ao lado de "Qualquer pessoa" e "Rodar".
  - Espalhar as semanais do modelo pelos dias da semana.
  - Quando entra o 2.º adulto, perguntar "Pôr «Tirar o lixo» em rotação entre vocês?". No modelo "Casa partilhada" a rotação é a razão de ser.

### UX-04 — Não há aviso de privacidade, termos nem consentimento parental antes de pôr dados de crianças
- **Onde:** o registo (`auth_screen.dart`) não tem ligação para a política de privacidade nem aceitação de termos (`grep` sem resultados para "privacidade/termos/consentimento" na app). O perfil de criança (`household_screen.dart:657-712`) pede só o nome e o PIN, sem qualquer declaração de quem dá o consentimento. A própria especificação ("Riscos — Dados de menores") diz que abaixo dos 13 anos é preciso o consentimento dos pais.
- **Cenário:** no grupo de testes, uma família cria perfis de filhos com 6 e 9 anos e fotos de prova das tarefas. Não existe registo de consentimento nem informação sobre o tratamento desses dados. Pelo RGPD e pelas regras da Apple e da Google, isto bloqueia o lançamento e também é um risco de confiança: o produto diz que "a confiança é central".
- **Correção:** uma política de privacidade e termos curtos em pt-PT e inglês, ligados no registo ("Ao criar conta aceitas…") e na Ajuda. No perfil de criança, uma caixa obrigatória "Sou o pai/mãe ou tutor legal e autorizo…", gravada com a data e com quem a deu, a data de nascimento (a API já a aceita, a app não a pede) e um texto que diga o que fica guardado (fotos durante 90 dias, pontos, histórico). Para o teste fechado, um consentimento informado de participação.

---

## Alto

### UX-05 — Convidar a família tem passos a mais e não há partilha nativa
- **Onde:** o convite só se abre pelo ícone de grupo sem texto na barra (`home_screen.dart:262-268`), seguido do botão "Convidar", da escolha do papel e de um diálogo com QR (`household_screen.dart:573-630`) que só tem "Copiar link"/"Copiar código". O `pubspec.yaml` não tem `share_plus`. O link só existe com `WEB_URL` (`invite_link.dart:5-9`) e só a app **web** o lê (`main.dart:29`, `kIsWeb`). Não há App Links nem Universal Links (`AndroidManifest.xml` só tem o LAUNCHER). Depois de criar a casa não aparece nenhum passo seguinte.
- **Cenário:** o pai quer convidar a mãe pelo WhatsApp. Copia o código, muda de app, cola e explica "instala o Kubico e escreve isto". A mãe toca no link e abre-se o browser, não a app. Na app nativa tem de criar conta, ver o ecrã "Criar uma casa" (o primeiro cartão) e encontrar mais abaixo "Entrar com um convite". Com convite pendente na web, o cartão de criar casa continua em cima (`onboarding_screen.dart:102-191`), e é fácil criar uma segunda casa por engano.
- **Correção:**
  - Um botão "Partilhar convite" com a folha nativa e uma mensagem pronta ("A Ana convidou-te para a casa «Casa da Amora» no Kubico: <link>"), com o link a apontar para uma página que leva à loja e que, já com a app instalada, a abre (App Links/Universal Links).
  - Com convite pendente, o onboarding mostra **só** "Entrar em «X» como Adulto?".
  - Depois de criar a casa, uma lista de primeiros passos: "Convida quem vive contigo", "Cria os perfis das crianças", "Liga o teu calendário".

### UX-06 — O desfazer existe, mas não está onde se erra
- **Onde:**
  - Arrastar apaga o artigo sem confirmação nem "Desfazer" (`shopping_items_view.dart:351-360`), e "Limpar" o carrinho também (`:230-236`).
  - "Apagar" no editor de tarefas apaga logo, sem confirmação (`chore_editor.dart:322-327`, seguido de `chores_view.dart:279-281`). Já as listas pedem confirmação (`shopping_lists_view.dart:104`), o que é inconsistente.
  - Concluir uma tarefa mostra "+2 pontos!" sem ação (`chores_view.dart:208-224`).
  - O desfazer só se encontra em ⋮ → "Histórico de alterações" (`activity_screen.dart:167`).
- **Cenário:** uma criança com "Participar" em Compras arrasta sem querer 3 artigos para fora da lista. O pai não sabe que o histórico existe e torna a escrevê-los. Uma criança marca a tarefa do irmão por engano (se não tiver responsável) e não consegue voltar atrás.
- **Correção:** um `SnackBar` com "Desfazer" em todas as ações destrutivas e em concluir. Usa o `/activity/{id}/undo` que já existe, ou o inverso local nas compras. Também confirmar ao apagar uma tarefa, ou pelo menos dar o "Desfazer".

### UX-07 — Os avisos não levam a lado nenhum e não há lembretes por hora
- **Onde:** tocar num aviso só o marca como lido (`notifications_screen.dart:87`). Não há tratamento de `onMessageOpenedApp`/`getInitialMessage` (`grep` vazio em `src/notifications`), por isso tocar no push abre a app em Compras. `NotificationKind` (`backend/src/Kubico.Domain/Notifications/Notification.cs:3-28`) só tem avisos de alterações. Não há "o evento começa daqui a 30 min", "a rotina da noite ainda não está feita", "a tarefa está em atraso", "a Rita pediu uma recompensa", nem "a mãe entrou na casa".
- **Cenário:** às 18h00 o pai recebe "A Rita pediu «Meia hora extra de ecrã»". Toca no aviso, abre a lista de compras e tem de descobrir que tem de ir a Recompensas. A natação de quinta passa sem lembrete, e é por isto que as famílias ficam com o Cozi ou com o calendário do telemóvel.
- **Correção:** cada aviso leva um destino (área + id) e o toque abre-o, também a partir do push. Lembretes de eventos (por omissão 30 min antes, por evento) e um "fecho do dia" para as crianças (às 19h, "falta a rotina da noite"), com respeito pelas horas de silêncio e pelo resumo diário que já existem.

### UX-08 — Não há modo simples para avós e crianças pequenas (requisito transversal)
- **Onde:** não existe (`grep` sem resultados para "simples/kiosk/tablet" na UI). Um avô entra como Adulto, com acesso total ("Gerir" em tudo, `ModulePermissions.cs:40-44`) e com o ecrã completo: 4 separadores, um menu ⋮ com 7 entradas, "Terminar sessão em todos os dispositivos" e formulários longos. O modelo "Cuidar dos pais ou avós" cria as tarefas mas não pensa em como o próprio avô usa a app.
- **Cenário:** a avó Lurdes, de 78 anos, quer ver se alguém já comprou os medicamentos e confirmar a visita de domingo. Toca numa tarefa para a marcar e abre-se o editor, porque em `chores_view.dart:367` tocar na linha de quem gere abre a edição. Altera o prazo sem querer. Ou arrasta e apaga um artigo.
- **Correção:**
  - Um "Modo simples" por membro, escolhido pela pessoa ou pelo administrador: uma só coluna com "Hoje", "A minha lista" e "Pedir ajuda", botões grandes com texto, sem gestos escondidos, sem menu ⋮ e com confirmação no que apaga. Também serve para crianças dos 5 aos 8 anos, com ícones nas tarefas.
  - Sugerir o papel com acesso de "Participar" para avós em vez de Adulto.
  - A prazo, um modo tablet de cozinha (painel fixo com Hoje, Calendário, Lista e troca rápida de criança), que é diferenciador na especificação.

### UX-09 — Leitores de ecrã e texto grande: nada foi pensado de propósito
- **Onde e cenários:**
  - Não há nenhum `Semantics`/`semanticLabel` na app.
  - Apagar um artigo só se faz com um gesto (`Dismissible`, `shopping_items_view.dart:351`), sem alternativa no TalkBack ou VoiceOver.
  - As células do mês têm 48 px fixos com o número e pontos de cor (`calendar_view.dart:541-555`). Com texto a 200% sobrepõem-se, e o leitor diz só "25", sem "hoje", "feriado" nem "3 eventos".
  - As horas dos eventos encolhem com `FittedBox` (`calendar_view.dart:640-650`), o que anula a letra grande de propósito.
  - Larguras fixas de 96 e 120 px no resumo de pontos (`chores_view.dart:539,552`) cortam "120 pts · 15 tarefas".
  - A linha "Código da família: XXXX [copiar]" é uma `Row` sem `Flexible` (`household_screen.dart:519-536`) e transborda com letra grande.
  - Quem é responsável por um evento só se vê pela cor (barra de 4 px) e pela inicial, e quem não distingue cores perde a história 6.
  - O botão de concluir uma tarefa é um ícone pequeno à esquerda e o resto da linha faz outra coisa.
- **Correção:** um passe de acessibilidade com um teste de widget a `textScaler` 2.0 nos ecrãs principais. Rótulos semânticos nas células do mês, nas linhas de evento ("Natação da Rita, 18h às 19h, leva o pai") e nas tarefas ("Rotina da manhã, da Rita, 2 de 3 passos, 2 pontos"). `CustomSemanticsAction` (ou um botão) para apagar artigos. Nomes ao lado da cor. Uma volta com o TalkBack ligado antes do grupo de testes.

### UX-10 — Compras: ordenar por categoria quase não funciona, e não se edita um artigo
- **Onde:** `_add` e o ditado criam artigos sem categoria (`shopping_items_view.dart:141-147, 203-207`). A categoria só vem do código de barras (`ScannedProductDialog`). Tocar num artigo só o marca (`:361-372`), e não há como mudar o nome, a quantidade ou a categoria (a API aceita-o, `ShoppingEndpoints.cs:317-330`). Não há sugestões pelo histórico (MVP na especificação) nem aviso de duplicados ao escrever (só ao ler o código de barras, `:167-171`). Ver quem pôs cada artigo (`addedByUserId`) não aparece.
- **Cenário:** no supermercado, a lista sai pela ordem em que foi escrita ("leite, detergente, pão, iogurtes, lixívia"). Alguém escreve "2 leite" quando já lá estava "leite". Para corrigir "lete" tem de se apagar e escrever outra vez.
- **Correção:** uma categoria automática a partir de um dicionário pt-PT dos ~300 artigos mais comuns (Laticínios, Padaria, Limpeza…), ajustável pela casa e guardado como os `HouseholdProduct`. Edição ao manter premido. Sugestões dos artigos mais comprados ao escrever. Aviso "«Leite» já está na lista" com a opção de somar a quantidade.

### UX-11 — Perfil de criança num tablet partilhado: trocar de criança é penoso e o menu dela é o de um adulto
- **Onde:**
  - A entrada pede o código da família escrito à mão, sem QR (`child_sign_in_screen.dart:94-102`), depois o nome e o PIN.
  - Para mudar de criança é preciso "Terminar sessão" e repetir tudo.
  - O menu ⋮ da criança é igual ao do adulto (`home_screen.dart:283-291`): "Dispositivos com sessão iniciada", "Terminar sessão em todos os dispositivos", "Histórico de alterações".
  - O código da família está escondido atrás de "Mostrar o código da família" (`household_screen.dart:546-549`).
- **Cenário:** na sala, a Rita e o Tomás partilham o tablet. Para o Tomás marcar a rotina, alguém tem de terminar a sessão da Rita e escrever o código "K7M2QX9P". À terceira manhã os pais deixam de usar os perfis e marcam eles por todos ("Quem fez?"), e perde-se a autonomia que era o objetivo.
- **Correção:**
  - Um seletor "Quem és tu?" que se mantém no aparelho depois da primeira entrada, com o código lembrado, os avatares grandes e o PIN em teclado numérico grande.
  - "Mudar de criança" no lugar de "Terminar sessão".
  - Mostrar o código da família também em QR.
  - Na criança, esconder Dispositivos, "terminar em todos" e o Histórico.

---

## Médio

### UX-12 — Tarefas: interações inconsistentes e ruído para quem não gere
- Tocar na linha **edita** quem gere a tarefa (`chores_view.dart:367`) e não faz nada aos outros, enquanto nas Compras tocar na linha **marca** (`CheckboxListTile`). O alvo de concluir é o ícone pequeno (`:431-447`). Uma rotina tem os passos como `CheckboxListTile` (`:487-499`) e a linha principal não tem caixa, o que confunde.
- O ranking "Últimos 30 dias" entre irmãos aparece a todos (`:332-338`), incluindo às crianças. Há famílias que o vão querer desligar (comparação e conflitos), por isso convém uma opção por casa.
- A tarefa não recorrente desaparece ao concluir (`:134-136`). Não há "Feitas hoje" para a criança ver o que já fez, e o reforço positivo é só um snackbar.
- **Correção:** tocar na linha conclui (ou abre o detalhe com um "Marcar como feita" grande) e editar passa para um ícone ou para manter premido. Uma secção dobrável "Feitas hoje" e a opção "Mostrar o ranking às crianças".

### UX-13 — Fora das compras não há nada sem rede
- **Onde:** tarefas, calendário e recompensas leem sempre do servidor (`chores_view.dart:89-106`, `calendar_view.dart`). Sem rede aparece "Sem ligação ao servidor. Verifica a tua internet." (`api_client.dart:650`) e um ecrã vazio com "Tentar de novo".
- **Cenário:** um tablet no quarto, com Wi-Fi fraco, às 7h30: a criança não vê a rotina. Na casa de férias sem rede, não se vê o calendário.
- **Correção:** guardar no dispositivo a última cópia das tarefas da semana e do calendário (só leitura, com a indicação "visto às 07:12"). Numa segunda fase, concluir tarefas sem rede pela mesma fila das compras. O requisito "offline-first" é transversal na especificação.

### UX-14 — Casa partilhada (flatmates): a app fala de pontos e mesadas a adultos
- **Onde:** o administrador vê sempre "Recompensas" (`ModulePermissions.Resolve`: o Admin tem sempre Manage) e não há maneira de desligar uma área para a casa toda. O modelo "Casa partilhada" não traz rotação (UX-03). As despesas partilhadas, o principal motivo de um flatmate usar uma app destas (Splitwise, Flatastic), estão em V3.
- **Correção:** áreas ligadas por casa no modelo ("Casa partilhada" e "Casal" sem Recompensas, que se podem ligar depois). No grupo de testes, recrutar sobretudo famílias com crianças e dizer aos flatmates que as despesas não estão incluídas, para não medir a retenção de um segmento que o MVP não serve.

### UX-15 — Não se mudam nomes, nem o da própria pessoa nem o das crianças
- **Onde:** `account_screen.dart:110-111` mostra o nome sem editar, e o servidor não tem um endpoint para o nome de um adulto. Para as crianças a API existe (`ChildProfileEndpoints.cs:42`, `PUT /children/{id}` com o nome e a data de nascimento), mas a app não a usa. Também não se escolhe a cor nem o avatar.
- **Cenário:** um perfil criado como "tomas" só se corrige apagando-o, e com isso perdem-se os pontos e o histórico. "Pai" e "pai" ficam assim para sempre.
- **Correção:** "Editar perfil" (nome e cor) em "A minha conta" e no menu de cada criança (nome e data de nascimento).

### UX-16 — Textos de papéis desatualizados e convidados sem limite
- `household_screen.dart:565-570`: "Criança: Vê e adiciona artigos, sem apagar listas" e "Adulto: Cria e gere listas e conteúdos" são de quando só havia Compras. Hoje a criança faz tarefas e pede recompensas. Quem convida escolhe o papel com base nisto.
- O papel Convidado tem "Participar" em Recompensas por omissão (`ModulePermissions.cs:43`): a ama vê e troca pontos. O Convidado também não tem validade, embora a especificação a preveja ("Validade (convidados)"), e a ama do verão continua lá em dezembro.
- **Correção:** reescrever as descrições a partir das permissões efetivas da casa (ex.: "Tarefas: faz as suas · Calendário: só vê"). Convidado sem Recompensas por omissão e com "acesso até…" opcional ao convidar.

### UX-17 — A ligação ao Google, ao Outlook e ao iCloud está escondida num ícone de "partilhar"
- **Onde:** `calendar_view.dart:361-365` (ícone `share_outlined`, "Ver noutros calendários"), depois um diálogo e só então o botão "Ligar ao Google Calendar…" (`calendar_feed_dialog.dart:77-88`).
- **Cenário:** a história 7 está feita, mas os pais não a encontram e continuam a escrever os eventos duas vezes. As instruções do iPhone (`calendar_feed_dialog.dart:117`) já não correspondem ao caminho do iOS 17/18 (Definições → Apps → Calendário).
- **Correção:** um cartão no calendário vazio ("Traz os teus eventos do Google, do Outlook ou do iPhone") e um passo nos primeiros passos (UX-05). Rever o texto do iPhone.

### UX-18 — Erros silenciosos e validações fracas no primeiro uso
- "Criar casa" e "Entrar na casa" com o campo vazio não fazem nada (`onboarding_screen.dart:138, 183`), sem mensagem nem foco no campo. Um email só tem de ter "@" (`auth_screen.dart:181`).
- **Correção:** desativar o botão ou mostrar "Dá um nome à casa." e validar o formato do email (sem MX).

### UX-19 — O grupo de testes não vai ter dados para decidir
- Não há telemetria nem relatórios de falhas (`grep` sem Crashlytics, Sentry, Application Insights nem OpenTelemetry). A especificação define métricas (agregados ativos por semana, membros por agregado, retenção às 4 e às 12 semanas) e a meta dos 2 minutos, mas nada as mede.
- **Correção:** antes do teste, eventos anónimos mínimos e com consentimento (conta criada, casa criada e modelo, convite criado, convite aceite, 1.ª tarefa concluída por uma criança, 1.º artigo marcado), os tempos entre eles e relatórios de falhas. Um painel simples com estes dados chega.

---

## Baixo

### UX-20 — Plurais e consistência dos textos (pt-PT e inglês)
- Plurais partidos: "+1 pontos!" (`chores_view.dart:215-217`, en "+1 points!", `strings_en.dart:277`), "Trocar 1 pontos?" (`rewards_view.dart:107`), "Faltam 1 pontos" (`rewards_view.dart:309`), "{0}: +1 pontos!".
- Aspas inconsistentes: `"${list.name}"` e `"${reward.title}"` (`shopping_lists_view.dart:104`, `rewards_view.dart:109`) contra «…» no resto.
- "Tentar de novo" (botões) contra "Tenta outra vez" (`theme.dart`, erro genérico).
- Dívida de pontos: "Deves N" (`rewards_view.dart:264`) contra "deve N pts" (`theme.dart:54`).
- "babysitter" (`household_screen.dart:569`): em pt-PT, "ama" ou "baby-sitter".
- O tom (tu, frases curtas e explicativas) é bom e consistente, e as confirmações destrutivas dizem bem as consequências (ex.: tirar um membro, apagar a casa). A tradução inglesa está adaptada ("E.g. The Smiths").
- **Correção:** um `plural(n, um, outros)` com chaves `{0}` de singular e plural no `strings_en.dart`, e um teste de lint para `"` dentro de `.tr`.

### UX-21 — Pequenas fricções
- O nome da secção "Crianças sem email" está bem, mas o texto de ajuda é longo e técnico. Melhor um passo a passo com 3 ícones.
- O menu do utilizador mostra o email como primeira entrada desativada. Seria melhor um cabeçalho com nome e avatar, que também leva a "Editar perfil" (UX-15).
- Os ecrãs de "a carregar" mostram só a rodinha (sem esqueleto): é aceitável.
- A entrada de criança mostra os nomes de todos os perfis a quem tiver o código da família. É aceitável num código partilhado, mas convém dizê-lo aos pais ao gerar o código.

---

## O que falta para ser melhor que Cozi, FamilyWall e OurHome

| Concorrente | O que faz bem e falta ao Kubico | Onde o Kubico já ganha |
|---|---|---|
| **Cozi** | Agenda "Hoje" e widget, lembretes de eventos, partilha por email, plano de refeições | Sincronização nos dois sentidos (o Cozi só importa), boleias e conflitos, feriados PT, permissões |
| **OurHome** | Ecrã de criança com ícones grandes, uma tarefa por criança, pontos visuais, alternar perfis num aparelho | Mesada com períodos e pagamentos, fotos de prova, ausências que tiram da rotação, desfazer |
| **FamilyWall** | Mensagens e mural, localização, documentos, partilha nativa de convites | Lista sem rede, código de barras e voz, RGPD (exportar e apagar), 2FA |
| **AnyList** | Sugestões e corredores automáticos, editar artigos, receitas e lista | Tempo real com o nome da casa por código de barras |

Para **igualar** no essencial: ecrã Hoje (UX-02), lembretes (UX-07), convite partilhável (UX-05), experiência de criança (UX-03, UX-11), compras com categorias e sugestões (UX-10). Para **ultrapassar**, com o que já está construído: um modo tablet de cozinha (o motor de tempo real e as permissões já servem), a vida em Portugal (feriados municipais, e as fases seguintes de IRS, IPO e IUC) e o histórico com desfazer à vista em toda a app.

## Pronto para um primeiro grupo de testes com famílias reais?

Ainda não. Antes de convidar famílias:
1. UX-01 (recuperar a conta) e UX-04 (privacidade e consentimento): bloqueiam.
2. UX-03 (o modelo funciona para 2 ou mais crianças) e UX-02 (ecrã Hoje e "as minhas tarefas").
3. UX-05 (partilhar o convite e abrir a app pelo link) e UX-19 (medir).
4. Uma volta guiada de 30 min com 2 famílias (pai, mãe, criança de 7 a 10 anos, de preferência um avô), com o TalkBack e a letra grande ligados durante 5 minutos.

Perfil do grupo: 8 a 12 famílias com crianças dos 6 aos 12 anos e Android (enquanto não houver push no iPhone), 2 semanas, com uma entrevista curta ao dia 3 e ao dia 14.

## Top 5

1. **UX-01:** recuperar e mudar a palavra-passe e verificar o email (ou entrar com Google ou Apple).
2. **UX-02:** um ecrã **Hoje** como entrada e "As minhas tarefas" para as crianças.
3. **UX-03 + UX-11:** a experiência de criança a sério: rotinas por criança no modelo, a opção "uma para cada pessoa" e alternar perfis no tablet.
4. **UX-05:** convite com a folha de partilha nativa, links que abrem a app e primeiros passos depois de criar a casa.
5. **UX-04 + UX-06:** privacidade e consentimento parental, e "Desfazer" no snackbar em todas as ações destrutivas e ao concluir.
