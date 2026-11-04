# Kubico — Revisão fase 3: design e visual (DES)

Revisor: design de produto/UI sénior. Base: branch `mvp-continuacao` @ 60bb5a1, `app/lib/src/ui/*` (todos os ecrãs), `web/`, `android/res`, paleta de membros do servidor.
Método: leitura de todos os ecrãs; tokens reais do tema calculados com um teste Flutter numa pasta temporária (`ColorScheme.fromSeed(0xFFC8553D)`, claro e escuro); contraste WCAG 2.2 calculado com `Color.computeLuminance()`. Todos os números de contraste deste documento saíram desse cálculo. O repositório não foi alterado.

---

## 0. Em resumo

A app está funcionalmente rica e já é coerente com o Material 3, mas hoje parece uma app Material genérica, não a "casa" calorosa e de confiança que a especificação descreve:

- **Não há marca.** Os ícones da app e da web são o logótipo do Flutter, a cor do manifesto web é o azul do Flutter e a cor da marca (terracota `#C8553D`) chega ao ecrã como um castanho `#904B3C`.
- **As cores dos membros falham o contraste.** Nenhuma das 8 cores atuais chega a 4,5:1 com as iniciais a branco. A pior, `#F2CC8F`, fica em 1,52:1. Essas cores são muitas vezes a única pista visual de quem é quem.
- **Não há ecrã "Hoje" nem painel de cozinha.** A app abre em Compras e, num tablet, mostra a mesma lista com uma barra lateral. O diferenciador central da especificação (o modo tablet de cozinha) não tem ainda desenho.
- **A gamificação das crianças é só texto**: um SnackBar "+5 pontos!" e um número grande. Não há progresso até à recompensa nem celebração. O resumo do mês é um ranking entre irmãos.
- **Com texto grande e com avós, a app falha**: alturas e larguras fixas, `FittedBox` que anula a escala do texto, `outline` usado como cor de texto (4,05:1) e densidade compacta no Windows, onde a cozinha pode ter um tablet tátil.

A proposta (secções 2 a 5) define um design system "Kubico" com tokens próprios (sem depender só do `fromSeed`), uma paleta de 11 cores de membro com tons para claro e escuro, todas verificadas a ≥ 4,5:1, uma tipografia arredondada e legível, componentes partilhados (`MemberAvatar`, `StateView`, `ChoreCard`, `PointsBadge`…) e o redesenho de 5 ecrãs: **Hoje** (novo), **Compras**, **Tarefas**, **Calendário** e **Painel de cozinha** (novo).

---

## 1. Diagnóstico (por gravidade)

### Alto

**DES-01 — A identidade visual não existe fora do nome.**
- `app/web/icons/Icon-192.png`, `Icon-512.png`, `Icon-maskable-*.png`, `web/favicon.png` e `android/app/src/main/res/mipmap-*/ic_launcher.png` são o logótipo do Flutter (verificado visualmente no Icon-192).
- `app/web/manifest.json:6-7`: `background_color`/`theme_color` `#0175C2` (o azul do Flutter). Na web instalada e no Android, a barra do sistema e o ecrã de arranque saem azuis.
- `backend/src/Kubico.Api/Calendar/Sync/CalendarSyncEndpoints.cs:552-557`: a página de confirmação do Google/Outlook usa o roxo `#6750a4` do Material por omissão. É precisamente a página onde a pessoa tem de confiar no Kubico para ligar a sua conta.
- `app/lib/src/ui/auth_screen.dart:153-155`: o "logótipo" é `Icons.cottage_rounded` com a palavra "Kubico" em `headlineMedium` (a letra do sistema).
- `app/android/app/src/main/res/values/styles.xml`: `LaunchTheme` com o fundo branco por omissão, sem marca.

Cenário: um avô instala a app que o neto recomendou e vê um ícone que é igual ao de dezenas de apps de exemplo. Isto não inspira confiança nem é memorável. Numa app que guarda dados de crianças, a confiança começa no ícone.
Correção: secção 2.1 (marca) e DES-22 (manter a web alinhada).

**DES-02 — As cores dos membros não têm contraste suficiente e não se adaptam ao modo escuro.**
`backend/src/Kubico.Api/Households/HouseholdEndpoints.cs:61` define 8 cores e a app desenha as iniciais a branco por cima (`foregroundColor: Colors.white` em 10 sítios, ver DES-10).

| Cor atual | Branco sobre a cor (texto ≥ 4,5) | Cor sobre o cartão claro `#FFF0EE` (gráfico ≥ 3) | Cor sobre o cartão escuro `#231917` |
|---|---|---|---|
| `#4F7CAC` | 4,36 ✗ | 3,94 ✓ | 3,93 ✓ |
| `#E07A5F` | 2,95 ✗ | 2,66 ✗ | 5,82 ✓ |
| `#81B29A` | 2,40 ✗ | 2,16 ✗ | 7,17 ✓ |
| `#F2CC8F` | **1,52 ✗** | **1,37 ✗** | 11,29 ✓ |
| `#9C6ADE` | 3,80 ✗ | 3,43 ✓ | 4,52 ✓ |
| `#3D9970` | 3,51 ✗ | 3,16 ✓ | 4,90 ✓ |
| `#D96C9C` | 3,19 ✗ | 2,88 ✗ | 5,38 ✓ |
| `#5BC0BE` | 2,16 ✗ | 1,95 ✗ | 7,96 ✓ |

- 8 de 8 falham com as iniciais a branco, e 5 de 8 falham como elemento gráfico em claro: os pontos do mês (`calendar_view.dart:570-575`, 6 px), a barra do evento (`calendar_view.dart:633-637`, 4 px) e as barras de progresso (`chores_view.dart:543-548`).
- A cor é muitas vezes a única forma de distinguir a pessoa: os pontos do mês não levam nome e os chips de participantes (`chore_editor.dart:222`, `calendar_event_editor.dart:350`, `rewards_view.dart:283`) mostram um círculo sem inicial.
- No modo escuro usam-se os mesmos hex: o branco sobre `#F2CC8F` continua ilegível.

Correção: secção 2.3 (paleta de membros com pares tonais e chave em vez de hex).

**DES-03 — O `ColorScheme.fromSeed` desvirtua a marca e não dá cores semânticas.**
`app/lib/src/ui/theme.dart:7-10`. Resultado real, calculado:
- Em claro, `primary` = `#904B3C` (castanho escuro, não terracota). Em escuro, `#FFB4A4` (salmão).
- `primaryContainer` e `secondaryContainer` são **o mesmo** `#FFDAD3`: o banner "sem ligação" (`shopping_items_view.dart:625-626`, `secondaryContainer`), o dia selecionado (`calendar_view.dart:532`, `primaryContainer`) e os botões tonais ficam todos da mesma cor rosada. A app é monocromática.
- Não existem `success`, `warning` nem `info`. Por isso:
  - os **feriados** usam `colorScheme.error` (`calendar_view.dart:421` e `528`), ou seja, o 25 de Abril aparece a vermelho de erro;
  - **"Em atraso"** usa o mesmo vermelho (`chores_view.dart:354`), tal como o ícone de conflito (`calendar_view.dart:663`) e o "Recusar" (`rewards_view.dart:355`). Quatro significados diferentes com uma só cor.
- As quatro áreas (Compras, Tarefas, Calendário, Recompensas) não têm cor própria. Para crianças e avós, a cor da área é a forma mais rápida de saber "onde estou".

**DES-04 — Não há ecrã "Hoje" / Início.**
`home_screen.dart:50`: `HouseholdModule _section = HouseholdModule.shopping;`. A app abre sempre na lista de compras. Para saber "o que há hoje" (tarefas, eventos, boleias, quem está ausente, pedidos à espera de aprovação), a pessoa tem de visitar 3 a 4 separadores. Os concorrentes (Cozi, FamilyWall) abrem num resumo do dia. É também esse resumo que serve de base ao painel de cozinha (DES-05).

**DES-05 — O modo painel de cozinha (diferenciador da especificação) não existe no desenho.**
- O layout largo (`home_screen.dart:205-236` e `295-315`) é só uma `NavigationRail` com a lista de compras a 340 px e o detalhe ao lado. Nas outras áreas, o conteúdo de telemóvel fica esticado até 1280 px ou mais (por exemplo, os cartões das tarefas com o título à esquerda e os pontos lá muito longe, à direita).
- Não há modo de ecrã sempre ligado, letra para ler a 2–3 m, troca rápida de "quem está a usar", modo noite nem proteção contra toques acidentais nas ações de administração.

**DES-06 — Com texto grande (avós, baixa visão), os ecrãs partem ou anulam a escala.**
- `calendar_view.dart:541`: `SizedBox(height: 48)` na célula do mês, com o número e os pontos. A 200 % o número sai da célula ou é cortado.
- `calendar_view.dart:640-651`: as horas dentro de `FittedBox(fit: BoxFit.scaleDown)` com 52 px. A letra grande é encolhida de volta, o que anula a escolha de acessibilidade da pessoa.
- `chores_view.dart:539` e `552`: `SizedBox(width: 96)` com o nome e `SizedBox(width: 120)` com "12 pts · 3 tarefas". A 130 % já corta.
- As iniciais dos avatares têm `fontSize: 11/12` fixo dentro de círculos de 24–28 dp (`calendar_view.dart:672`, `chores_view.dart:173` e `473`, `allowance_section.dart:194`). O `textScaler` aumenta a letra e o círculo não cresce.
- `theme.dart:14`: `VisualDensity.adaptivePlatformDensity` dá densidade **compacta** no Windows, macOS e Linux. Num tablet Windows tátil na cozinha, os alvos encolhem para cerca de 40 dp.

**DES-07 — A gamificação para crianças é só texto e compara irmãos.**
- Concluir uma tarefa dá um `SnackBar` "+5 pontos!" (`chores_view.dart:208-224`, `253-261`): 4 s de texto em baixo, sem animação, som nem háptica, e lido de forma diferente pelos leitores de ecrã.
- `rewards_view.dart:296-320`: a recompensa diz "Faltam 12 pontos" em texto, sem barra de progresso nem imagem. O botão fica desativado (cinzento), o que para uma criança lê-se como "não posso".
- `rewards_view.dart:263-266`: o saldo é um `displaySmall` sem moldura. A dívida aparece como "Deves 5" ao lado do mesmo número grande, com o mesmo peso visual.
- `chores_view.dart:507-566` ("Últimos 30 dias"): barras normalizadas pelo máximo, ou seja, um **ranking**. Numa família com uma criança de 5 e outra de 12 anos, a mais nova fica sempre com a barra curta. As boas práticas em apps de rotinas infantis vão para o progresso pessoal (eu face à minha meta e à minha sequência de dias), não para a competição.

### Médio

**DES-08 — Concluir uma tarefa tem pouca affordance e nenhum retorno visual.**
`chores_view.dart:431-447`: o botão de concluir é um `IconButton` com `radio_button_unchecked` (um círculo fino de 24 dp em `onSurfaceVariant`), igual a um radio de formulário. O `selectedIcon: Icons.check_circle` (`:446`) nunca aparece porque não há `isSelected`. "Atribuída a outra pessoa" e "Só podes ver" só se distinguem pelo cinzento do desativado e por uma tooltip, que não aparece com toque. Não há transição entre "por fazer" e "feita": a linha desaparece da lista (`_upsert` remove a tarefa, `:134-135`).

**DES-09 — Estados vazio, carregamento e erro: 4 implementações e nenhuma convida a agir.**
- `shopping_lists_view.dart:203-228` (`_Message` com ícone), `home_screen.dart:415-441` (`_NoAreas`), `home_screen.dart:443-460` (`_EmptyDetail`) e, nos outros ecrãs, só `Text(errorText)` com um `TextButton` sem ícone (`chores_view.dart:293-305`, `calendar_view.dart:372-384`, `rewards_view.dart:155-167`, `shopping_items_view.dart:471-483`).
- Há 15 `CircularProgressIndicator()` a ecrã inteiro e nenhum esqueleto. Ao mudar de separador, o ecrã pisca de vazio para conteúdo.
- Os estados vazios são uma frase ("Sem tarefas por fazer.", "A lista está vazia.") sem ilustração nem botão primário. O FAB está noutro canto e, para quem não gere, não existe.
- O erro de rede e a falta de acesso usam o mesmo tratamento visual.

**DES-10 — O avatar de membro está copiado 10+ vezes com variações.**
`CircleAvatar` com `colorFromHex` + inicial em `activity_screen.dart:151`, `allowance_section.dart:190`, `calendar_view.dart:668`, `child_sign_in_screen.dart:109` e `120`, `chores_view.dart:167` e `463`, `chore_history_screen.dart:261`, `household_screen.dart:442`, `rewards_view.dart:341`. Os raios variam (12/14/20/32) e a letra também (11/12/por omissão), e cada cópia repete `name.isEmpty ? '?' : name[0].toUpperCase()`, que parte com nomes começados por emoji ou letra composta (`name[0]` é uma unidade UTF-16). Os chips mostram só a cor, sem inicial (`chore_editor.dart:222`, `calendar_event_editor.dart:350`, `rewards_view.dart:283`). A ausência de uma pessoa (está fora) nunca se vê no avatar.

**DES-11 — A tipografia é a do sistema e a hierarquia é plana.**
- Sem `fontFamily` nem `textTheme` próprios: Roboto no Android e na web, Segoe UI no Windows. A app muda de cara entre plataformas e não tem calor.
- Os cabeçalhos de secção ("Hoje (3)", "Em atraso (1)", "Recompensas", o dia no calendário) são `titleSmall` 14 sp a 500, quase o mesmo peso do texto da linha (`bodyLarge` 16 sp). A hierarquia faz-se pelo tamanho ao contrário.
- Os números (pontos, horas, quantidades) não usam algarismos tabulares, por isso "11:10" e "18:45" ficam com larguras diferentes numa coluna.

**DES-12 — Calendário: pouca informação por ecrã e nenhum aproveitamento da largura.**
- No mês há no máximo 3 pontos de 6 px por dia (`calendar_view.dart:497`), sem "+2" nem título. Num tablet de 1280 px, a grelha do mês fica com células de 48 dp de altura e a lista do dia vem por baixo (`calendar_view.dart:390-401`), em vez de ao lado.
- Não há vista de semana com grelha de horas (a "semana" é uma lista de 7 dias).
- O hoje é só um contorno (`:535`) e o selecionado `primaryContainer`; nos dois casos o número tem a mesma cor.
- Conflito: ícone vermelho com `Tooltip` (`:660-664`), que é invisível com toque e não está ligado à semântica.
- A barra de ferramentas (`:329-370`) gasta cerca de 110 dp: `SegmentedButton` numa linha e a navegação na outra.

**DES-13 — Compras: a lista está ordenada por categoria mas não o mostra, e apagar não se desfaz.**
- `shopping_items_view.dart:604-607` ordena por categoria mas não desenha cabeçalhos. A categoria vai no subtítulo, repetida em cada linha.
- `:517-529`: o campo de adicionar tem 4 ícones (prefixo `add` e, como sufixo, scanner, microfone e `send`). Num telemóvel de 360 dp, o texto útil fica com cerca de 150 dp. O `Icons.send` (avião de papel) para "adicionar" é uma metáfora de mensagem.
- `:579-588`: o deslizar para apagar não oferece "Desfazer" (o servidor tem histórico com desfazer, mas a app não o liga aqui). Para crianças com o tablet, apagar por engano é provável.
- `shopping_lists_view.dart:148-163`: todos os cartões têm o mesmo ícone de carrinho e o caixote de lixo **sempre visível** em cada linha, uma ação destrutiva à distância de um toque.
- Não há progresso da lista ("8 de 14 no carrinho").

**DES-14 — A navegação esconde o essencial e mistura destrutivo com rotineiro.**
`home_screen.dart:269-291`: um menu ⋮ com 7 entradas (o email como título desativado, Histórico, Conta, Dispositivos, Ajuda, Terminar sessão e **Terminar sessão em todos os dispositivos**), sem ícones nem separadores. "Membros e convites" é um ícone `group_outlined` na barra. O seletor de casa (`:375-412`) é um `PopupMenuButton` com o título, que não parece tocável para quem não conhece o padrão.

**DES-15 — Usa-se `outline` como cor de texto e falha AA.**
`outline` em claro é `#85736F`: 4,28:1 sobre `surface` e **4,05:1** sobre os cartões (`surfaceContainerLow`). Usado para texto em `calendar_view.dart:427` ("Sem eventos."), `:528` (dias fora do mês), `shopping_items_view.dart:596` e `chores_view.dart:496` (riscado). O texto riscado já perde legibilidade; com 4,05:1, falha AA.

**DES-16 — Não há "modo simples" (requisito da especificação: avós e crianças).**
Não existe uma opção de letra maior, menos ações por ecrã ou ícones com texto. O perfil de criança vê a mesma interface do adulto, só com menos permissões.

**DES-17 — O login de criança usa o teclado do sistema e uma lista.**
`child_sign_in_screen.dart:104-118`: "Quem és tu?" é uma lista de `ListTile` com avatar de 40 dp. `:136`: o PIN é um `TextField` com `obscureText` e o teclado do sistema. Para uma criança de 6 anos, o padrão esperado são avatares grandes em grelha e um teclado numérico grande com pontos.

### Baixo

**DES-18 — Não há movimento nem micro-interações.** Não há nenhum `Animated*`, `Hero`, `HapticFeedback` nem `AnimatedSwitcher` em `lib/src/ui`. Marcar um artigo, concluir uma tarefa e trocar de vista do calendário são saltos secos. A preferência de reduzir o movimento já é respeitada na web (`web/index.html:64`), mas não na app.

**DES-19 — A iconografia mistura estilos.** Há `_outlined`, preenchidos e `_rounded` sem regra (`cottage_rounded`, `cottage_outlined` e `warning_amber_rounded` convivem com `task_alt`), `Icons.cleaning_services_outlined` para "Limpar carrinho" e `Icons.send` para adicionar.

**DES-20 — O banner de sincronização é discreto demais e da cor errada.** `shopping_items_view.dart:611-634`: um `ListTile dense` em `secondaryContainer` (o mesmo rosa do resto, DES-03). "Sem ligação" é um estado que a pessoa tem de perceber (as alterações ficam à espera), por isso pede a cor de aviso e um ícone com contraste.

**DES-21 — Avisos importantes vão em SnackBar.** Um conflito ao guardar um evento ("Atenção: Ana tem outro compromisso…", `calendar_view.dart:306-308`) desaparece em 4 s. Devia ficar no próprio evento (banner no cartão) e no editor antes de guardar.

**DES-22 — As cores da web estão escritas à mão e vão divergir.** `web/index.html:50-62` tem `#fff8f6`, `#8f3a26`, `#c8553d` e `#1a110f` copiados do `fromSeed`. Quando os tokens mudarem, o ecrã de carregamento fica com outras cores.

**DES-23 — `centerTitle: false` com o seletor de casa e 3 ações cortam o nome da casa no telemóvel.** `home_screen.dart:258-293`: a 360 dp ficam cerca de 170 dp para "Casa da Avó Lurdes ▾".

---

## 2. Proposta de design system "Kubico"

### 2.1 Marca

- **Conceito:** *kubico* é "casa" em quimbundo/português de Angola. O símbolo é **um telhado terracota sobre um quadrado arredondado** (a "casinha" de uma criança): um "K" estilizado em que a perna de cima é a água do telhado. Funciona a 16 px (favicon) e a 512 px.
- **Cores do símbolo:** telhado `#B4472F` (Terracota 600), corpo `#FFF8F3` (Creme), traço `#2A1F1A`. Ícone adaptativo do Android: fundo `#B4472F`, primeiro plano creme.
- **Logótipo:** símbolo + "kubico" em minúsculas, Nunito ExtraBold (800), com espaçamento de letras −1 %.
- **Tom de voz visual:** caloroso (creme e terracota em vez de branco puro e azul), arredondado (raios generosos), concreto (ilustrações de objetos da casa, não abstrações).
- **Aplicar em:** `web/icons/*`, `favicon.png`, `mipmap-*` (gerar com `flutter_launcher_icons` como dev_dependency, ou à mão), `manifest.json` (`theme_color: #B4472F`, `background_color: #FFF8F3`), `LaunchTheme` do Android (`windowBackground` creme + símbolo ao centro, com variante `values-night` em `#1B1512`), iOS `LaunchScreen.storyboard`, página de confirmação do sync (`CalendarSyncEndpoints.cs:552-557`: botão `#B4472F`, fundo `#FFF8F3`/`#1B1512`) e `web/index.html` (DES-22).

### 2.2 Cor: tokens

Princípio: **tokens escritos à mão** (não `fromSeed`), postos num `ColorScheme(...)` explícito e numa `ThemeExtension<KubicoColors>` para o que o M3 não tem (sucesso, aviso, informação, áreas e membros). Todos os pares texto/fundo foram verificados (valores em baixo).

#### Claro

| Token | Hex | Uso | Contraste verificado |
|---|---|---|---|
| `primary` | `#B4472F` | Terracota 600: botões, seleção, marca | branco 5,41 · surface 5,14 |
| `onPrimary` | `#FFFFFF` | | |
| `primaryContainer` | `#FFDBD1` | fundo de destaque, dia selecionado | com onPrimaryContainer 10,14 |
| `onPrimaryContainer` | `#5C1A0B` | | |
| `secondary` | `#2F6B4F` | Verde-folha: feito, compras | branco 6,29 · surface 5,99 |
| `secondaryContainer` | `#CDEBD8` | | com onSecondaryContainer 10,27 |
| `onSecondaryContainer` | `#0B3823` | | |
| `tertiary` | `#855500` | Âmbar-sol: pontos, recompensas | branco 6,37 · surface 6,06 |
| `tertiaryContainer` | `#FFE3A8` | | com onTertiaryContainer 11,19 |
| `onTertiaryContainer` | `#3D2800` | | |
| `error` | `#B3261E` | só erros e ações destrutivas | branco 6,54 |
| `errorContainer` / `onErrorContainer` | `#F9DEDC` / `#410E0B` | | 12,77 |
| `surface` | `#FFF8F3` | Creme: fundo da app | |
| `surfaceContainerLowest` | `#FFFFFF` | cartões do painel de cozinha | |
| `surfaceContainerLow` | `#FBF1EA` | cartões | |
| `surfaceContainer` | `#F6EAE1` | barras de navegação, campos | |
| `surfaceContainerHigh` | `#F0E2D8` | folhas, diálogos | |
| `surfaceContainerHighest` | `#EADBD0` | trilhos de progresso, chips | |
| `onSurface` | `#2A1F1A` | texto principal | surface 15,25 · low 14,41 |
| `onSurfaceVariant` | `#5A4A42` | texto secundário | surface 8,01 · highest 6,23 |
| `outline` | `#7A665C` | bordas, **texto terciário só sobre surface/low/container** | surface 5,14 · low 4,86 · container 4,58 |
| `outlineVariant` | `#D9C7BC` | divisores | |
| *ext* `success` / `successContainer` / `onSuccessContainer` | `#2E7D32` / `#C8E6C9` / `#0D3B12` | confirmado, aprovado | branco 5,13 · container 9,46 |
| *ext* `warning` / `warningContainer` / `onWarningContainer` | `#8A5300` / `#FFE0B2` / `#3D2600` | sem ligação, em atraso, conflito | branco 6,33 · container 11,22 |
| *ext* `info` / `infoContainer` / `onInfoContainer` | `#1F5F99` / `#D3E4FF` / `#0A2E52` | feriados, dicas | branco 6,66 · container 10,69 |

Regra: o `outline` sobre `surfaceContainerHighest` dá 4,00, por isso aí o texto secundário usa `onSurfaceVariant`. O `success` sobre `surfaceContainerHigh` dá 3,79, por isso só serve para ícones e texto grande.

#### Escuro

| Token | Hex | Contraste verificado |
|---|---|---|
| `primary` / `onPrimary` | `#FFB59F` / `#5A1B0A` | surface 10,63 · par 7,75 |
| `primaryContainer` / `onPrimaryContainer` | `#7E2E1B` / `#FFDBD1` | 7,11 |
| `secondary` / `onSecondary` | `#9FD4B6` / `#0B3823` | surface 10,81 |
| `secondaryContainer` / `onSecondaryContainer` | `#1F4D37` / `#CDEBD8` | |
| `tertiary` / `onTertiary` | `#F5C04A` / `#3D2800` | surface 10,77 |
| `tertiaryContainer` / `onTertiaryContainer` | `#5E3F00` / `#FFE3A8` | |
| `error` / `errorContainer` | `#FFB4AB` / `#8C1D18` | surface 10,64 |
| `surface` | `#1B1512` | |
| `surfaceContainerLowest` / `Low` / `` / `High` / `Highest` | `#120E0C` / `#231C18` / `#2A221E` / `#352C27` / `#40362F` | |
| `onSurface` | `#F3E6DD` | surface 14,77 |
| `onSurfaceVariant` | `#D6C3B8` | surface 10,63 · highest 6,92 |
| `outline` / `outlineVariant` | `#A28F84` / `#54463E` | surface 5,85 · container 5,05 |
| *ext* `success` / `warning` / `info` | `#8FD694` / `#FFB960` / `#9CCBFF` | todos ≥ 10:1 sobre surface |

**Painel de cozinha, modo noite** (DES-05): `surface` `#0F0B09`, texto `#F3E6DD` (16,0:1), brilho reduzido e sem brancos.

#### Cores por área (navegação, cabeçalhos, ilustrações)

| Área | Claro (ícone/indicador) | Contentor claro | Escuro | Racional |
|---|---|---|---|---|
| Hoje | `primary` `#B4472F` | `#FFDBD1` | `#FFB59F` | a casa |
| Compras | `secondary` `#2F6B4F` | `#CDEBD8` | `#9FD4B6` | fresco, mercado |
| Tarefas | `#1F5F99` (info) | `#D3E4FF` | `#9CCBFF` | calma, rotina |
| Calendário | `#7449B8` (roxo) | `#EBDFFB` | `#CDB4FF` | tempo |
| Recompensas | `tertiary` `#855500` | `#FFE3A8` | `#F5C04A` | ouro, estrelas |

A cor da área aparece no indicador da `NavigationBar`/`NavigationRail` (com `indicatorColor` por destino via `NavigationDestination` personalizado), no ícone do cabeçalho do ecrã e nas ilustrações dos estados vazios. **Nunca** se usa como fundo de texto longo.

### 2.3 Cores dos membros (corrige DES-02)

Mudança de modelo: o servidor guarda uma **chave** (`"azul"`, `"verde"`…) em vez do hex, e a app resolve-a no tom certo para o tema. Durante a transição, a app mapeia os hex antigos para chaves (tabela em baixo), por isso não é preciso migrar já. `NextColorAsync` passa a escolher de entre as 11 chaves.

Cada cor tem 4 tons:
- **forte** (claro): fundo do avatar com iniciais brancas, barra do evento, anel;
- **clara** (escuro): fundo do avatar com iniciais `#1B1512`;
- **suave claro**: fundo do bloco de evento ou chip, com texto `onSurface`;
- **suave escuro**: o mesmo no modo escuro, com texto `#F3E6DD`.

| Chave | Forte (claro) | Branco/forte | Clara (escuro) | Clara sobre `#1B1512` | Suave claro | Suave escuro | Hex antigo que substitui |
|---|---|---|---|---|---|---|---|
| `azul` | `#2F64A8` | 5,98 | `#A9C7FF` | 10,58 | `#DCE7FA` | `#1F3554` | `#4F7CAC` |
| `laranja` | `#A9500B` | 5,47 | `#FFB68A` | 10,59 | `#FBE2D0` | `#55290A` | `#E07A5F` |
| `oliva` | `#4F6B1F` | 6,08 | `#BFD88A` | 11,53 | `#E6EDD0` | `#2A3810` | `#81B29A` |
| `mostarda` | `#7A6200` | 5,87 | `#E3CB5C` | 11,15 | `#F4EBC4` | `#3D3200` | `#F2CC8F` |
| `roxo` | `#7449B8` | 6,20 | `#CDB4FF` | 9,95 | `#EBDFFB` | `#3A2560` | `#9C6ADE` |
| `verde` | `#2E7550` | 5,56 | `#8FD6AE` | 10,66 | `#D5EFDF` | `#173D2A` | `#3D9970` |
| `rosa` | `#B23A73` | 5,61 | `#FFB0D2` | 10,66 | `#FADDE9` | `#5A1D3A` | `#D96C9C` |
| `petroleo` | `#12737A` | 5,59 | `#7FD8DE` | 10,99 | `#D2EEF0` | `#0C3A3E` | `#5BC0BE` |
| `tijolo` | `#B23B3B` | 5,86 | `#FFB3AD` | 10,58 | `#FADCDA` | `#5A1E1E` | — |
| `indigo` | `#4A55B0` | 6,53 | `#BCC2FF` | 10,58 | `#E1E3FA` | `#262C5E` | — |
| `ameixa` | `#7B3F8C` | 7,16 | `#E7B3F5` | 10,46 | `#F3DEF8` | `#41204B` | — |

Verificações: o forte sobre `surface` claro dá ≥ 5,2 em todas (gráficos ≥ 3 e até texto). O claro sobre `surfaceContainerHighest` escuro dá ≥ 6,4. O suave com o texto da superfície dá ≥ 9,8 em claro e escuro, e o forte sobre o suave ≥ 4,4 (o título do evento pode ir na cor forte sobre o fundo suave). `tijolo` está perto do `error`: usar só se houver mais de 10 membros.

**Regra de redundância:** a cor de um membro nunca vai sozinha. Leva sempre inicial ou emoji (avatar), nome (tooltip *e* semântica) ou ambos. Os pontos do mês passam a "mini-avatares" de 10 dp com a primeira letra a partir de 14 dp de altura, ou uma pílula "+N".

Opcional, mas com grande impacto nas crianças: o membro escolhe um **emoji ou animal** como avatar (🦁, 🐢, 🌻…; campo `avatar` no `Membership`, só da lista que a app oferece, para não depender de fontes de emoji diferentes, desenhados como SVG próprios). O avatar mostra o desenho dentro do círculo da cor.

### 2.4 Tipografia

- **Títulos e números: Nunito** (variável, 400–800). Terminais arredondados, muito calorosa, ótima para crianças e com bom suporte de pt-PT.
- **Texto: Atkinson Hyperlegible Next** (400/500/700). Desenhada para baixa visão: distingue I/l/1 e O/0, ótima para avós e para ler ao longe.
- Ambas OFL. **Incluir como assets** (`assets/fonts/`, subconjunto latino estendido), sem `google_fonts` em tempo de execução, por causa da CSP da web (`connect-src`) e do modo sem rede. Peso estimado na web: cerca de 180 KB em woff2 para as 5 faces subconjuntadas. Se for demasiado, fica só a Nunito variável (cerca de 90 KB) para tudo.
- **Algarismos tabulares** (`FontFeature.tabularFigures()`) em horas, pontos, quantidades e contadores.

Escala (sp; `TextTheme` M3 com mapeamento próprio):

| Papel M3 | Família | Tamanho/altura | Peso | Uso |
|---|---|---|---|---|
| `displayLarge` | Nunito | 57/64 | 800 | relógio do painel |
| `displayMedium` | Nunito | 45/52 | 800 | saldo de pontos (criança) |
| `displaySmall` | Nunito | 36/44 | 700 | saldo de pontos, "Bom dia, Ana" no painel |
| `headlineMedium` | Nunito | 28/36 | 700 | título do ecrã "Hoje" |
| `headlineSmall` | Nunito | 24/32 | 700 | títulos de ecrã |
| `titleLarge` | Nunito | 22/28 | 700 | cabeçalhos de secção ("Hoje", "Em atraso") |
| `titleMedium` | Nunito | 17/24 | 700 | título do cartão, nome da tarefa |
| `titleSmall` | Nunito | 14/20 | 700 | rótulos de grupo, contadores |
| `bodyLarge` | Atkinson | 17/26 | 400 | texto de linha, artigos |
| `bodyMedium` | Atkinson | 15/22 | 400 | subtítulos |
| `bodySmall` | Atkinson | 13/18 | 400 | metadados (nunca < 13) |
| `labelLarge` | Nunito | 15/20 | 700 | botões |
| `labelMedium` | Nunito | 13/16 | 700 | chips, pílulas de pontos |
| `labelSmall` | Nunito | 12/16 | 700 | rótulos da navegação |

Os cabeçalhos de secção passam de 14 sp/500 para 22 sp/700 (DES-11), e o texto de linha para 17 sp (o M3 usa 16), mais confortável para avós.

**Modos de leitura** (DES-16), com um seletor em "A minha conta → Aspeto" guardado no dispositivo, como o `AppLanguage`:
- *Normal*: escala 1,0.
- *Conforto*: multiplicador 1,15 sobre o `textScaler` do sistema, alvos mínimos de 56 dp, menos metadados por linha, ícones sempre com rótulo.
- *Painel*: 1,35, só no modo cozinha.

Implementa-se com `MediaQuery.withClampedTextScaling` / `textScaler: TextScaler.linear(system * mode)`, com o teto em 2,0 para os layouts.

### 2.5 Espaçamento, raios, elevações e movimento

**Espaçamento (grelha de 4):** `space2=2, space4=4, space8=8, space12=12, space16=16, space20=20, space24=24, space32=32, space40=40, space48=48, space64=64`.
- Margem lateral do ecrã: 16 (compacto), 24 (médio), 32 (expandido), 40 (painel).
- Entre cartões: 12 (compacto), 16 (resto). Por dentro do cartão: 16 (20 no painel).
- Entre secções: 24 (o título da secção leva 24 por cima e 8 por baixo).
- FAB: 16 da margem. A lista termina com 96 por baixo (como já está).

**Raios:** `radiusXs=6` (pontos, pílulas pequenas), `radiusSm=10` (chips, campos), `radiusMd=16` (cartões, como hoje), `radiusLg=24` (folhas, diálogos, cartões do painel), `radiusXl=32` (cartão "herói" de pontos e mosaicos do painel), `radiusFull=999` (avatares, botões pílula). Os campos de texto passam a `OutlineInputBorder(borderRadius: 12)` preenchidos com `surfaceContainer` (hoje são retangulares, `theme.dart:15`).

**Elevação:** tonal primeiro (camadas de `surfaceContainer*`). As sombras só vão em 3 casos:
- `e1` (cartão levantado no painel e cartão que se arrasta): `0 1 3 rgba(42,31,26,.12)`;
- `e2` (FAB, barra de adicionar fixa): `0 4 12 rgba(42,31,26,.14)`;
- `e3` (celebração, menus): `0 12 32 rgba(42,31,26,.18)`.

No escuro não há sombra: sobe-se um nível de contentor.

**Movimento** (`KubicoMotion`):
- `short=150ms` (estado de toque, check), `medium=250ms` (entrada e saída de linhas, mudar de separador), `long=400ms` (celebração, transição de vista do calendário);
- curvas: `Easing.emphasizedDecelerate` para entrar e `Easing.emphasizedAccelerate` para sair, `Curves.easeOutBack` só no check e na estrela;
- se `MediaQuery.disableAnimationsOf(context)`: durações a 0, sem confettis, e a celebração passa a um selo estático.

**Hápticos:** `HapticFeedback.lightImpact()` ao marcar um artigo ou passo, `mediumImpact()` ao concluir uma tarefa, `heavyImpact()` ao trocar uma recompensa.

### 2.6 Iconografia e ilustração

- **Ícones:** só `Icons.*_rounded` (já vêm no Flutter, sem dependência nova). Não selecionado = rounded com contorno quando existe a variante, selecionado = rounded preenchido. Tamanhos: 20 (em linha), 24 (padrão), 28 (navegação no painel), 40 (mosaicos do painel).
- **Trocas:**
  - adicionar: `add_rounded` em vez de `send` (`shopping_items_view.dart:527`);
  - limpar carrinho: `remove_shopping_cart_rounded`;
  - Hoje: `home_rounded`;
  - Tarefas: `check_circle_rounded`;
  - Recompensas: `star_rounded` (as crianças percebem "estrelas" melhor do que "presente");
  - feriado: `celebration_rounded`;
  - conflito: `error_rounded` em `warning`;
  - ausente: `flight_takeoff_rounded`.
- **Ilustrações:** um conjunto de 8 ilustrações "spot" planas, com 2 tons da área e o creme, num estilo de recorte em papel com cantos arredondados: casa com telhado terracota (boas-vindas e sem áreas), cesto de mercado (lista vazia), vassoura e estrela (sem tarefas), calendário com sol (sem eventos), caixa de prémio (sem recompensas), nuvem a dormir (sem ligação), cadeado com coração (sem acesso) e lua com casinha (painel em modo noite). Vão em SVG com `flutter_svg`, ou como `CustomPainter` se se quiser evitar a dependência. Têm 120–160 dp e `ExcludeSemantics`.

### 2.7 Breakpoints e layouts

| Classe | Largura | Navegação | Grelha de conteúdo |
|---|---|---|---|
| Compacto | < 600 | `NavigationBar` com 5 destinos | 1 coluna |
| Médio | 600–839 | `NavigationRail` com rótulos | 1 coluna, máx. 640 centrada, ou 2 em Hoje |
| Expandido | 840–1199 | `NavigationRail` com FAB no topo do rail | 2 painéis (lista e detalhe) |
| Grande | ≥ 1200 | `NavigationDrawer` permanente com a casa e os membros | 2–3 painéis, máx. 1440 |
| **Painel** | qualquer largura, por opção | sem barra; mosaicos | grelha de 12 colunas, *gutter* 24 |

Hoje há um só breakpoint (`HomeScreen.wideLayoutBreakpoint = 840`, `home_screen.dart:33`), e Tarefas, Calendário e Recompensas não usam a largura. Cada lista de conteúdo deve ter `ConstrainedBox(maxWidth: 720)` no expandido quando não há painel de detalhe.

**Densidade:** `VisualDensity.standard` sempre (não `adaptivePlatformDensity`, DES-06) e `materialTapTargetSize: padded`. Alvos mínimos: 48 dp (normal), 56 dp (conforto e criança), 64 dp (painel).

---

## 3. Componentes-chave (para implementar em `lib/src/ui/design/`)

Estrutura proposta: `design/tokens.dart` (constantes), `design/kubico_theme.dart` (`buildTheme` + `ThemeExtension`s), `design/widgets/*.dart`. `theme.dart` fica com os utilitários atuais (`showError`, `pointsText`, …).

```dart
@immutable
class KubicoColors extends ThemeExtension<KubicoColors> {
  const KubicoColors({required this.success, required this.successContainer, required this.onSuccessContainer,
      required this.warning, required this.warningContainer, required this.onWarningContainer,
      required this.info, required this.infoContainer, required this.onInfoContainer,
      required this.areas, required this.members});
  final Color success, successContainer, onSuccessContainer, warning, warningContainer, onWarningContainer,
      info, infoContainer, onInfoContainer;
  final Map<KubicoArea, AreaColors> areas;        // ícone, contentor, onContentor
  final Map<String, MemberTones> members;         // chave → forte/clara/suave
  MemberTones member(String keyOrLegacyHex) => members[_legacy[keyOrLegacyHex.toUpperCase()] ?? keyOrLegacyHex]
      ?? members['azul']!;
  // copyWith / lerp ...
}

class MemberTones { // resolvido para o brilho do tema
  const MemberTones({required this.fill, required this.onFill, required this.soft, required this.onSoft});
  final Color fill;   // claro: forte; escuro: clara
  final Color onFill; // claro: #FFFFFF; escuro: #1B1512
  final Color soft;   // suave claro / suave escuro
  final Color onSoft; // onSurface
}
```

`ThemeData`: `colorScheme: ColorScheme(brightness: …, primary: …)` com os hex da secção 2.2, `fontFamily: 'AtkinsonHyperlegibleNext'`, `textTheme` da 2.4, `visualDensity: VisualDensity.standard`, `cardTheme` (raio 16, cor `surfaceContainerLow`, sem elevação), `filledButtonTheme` (altura mínima 48, forma `StadiumBorder`, `labelLarge`), `navigationBarTheme` (altura 72, `labelBehavior: alwaysShow`, indicador por área), `inputDecorationTheme` (preenchido, raio 12), `snackBarTheme` (flutuante, raio 12), `chipTheme` (raio 10), `listTileTheme` (`minVerticalPadding: 12`, `titleTextStyle: titleMedium`), `dividerTheme` (`outlineVariant`), `pageTransitionsTheme` (`FadeForwardsPageTransitionsBuilder` no Android e desktop).

**C1 `MemberAvatar`** (substitui as 10+ cópias, DES-10)
- `MemberAvatar(member, size: AvatarSize.sm|md|lg|xl, showAway: bool, ring: bool)`. Tamanhos 24/32/48/88 dp; a letra é 0,42 × o tamanho, com `textScaler: TextScaler.noScaling` (o círculo é um ícone, não texto corrido) e `Semantics(label: nome)`.
- Inicial: `member.displayName.characters.first.toUpperCase()` (grafemas, não `[0]`). Se houver emoji ou animal, desenha-o.
- `showAway`: um selo de 40 % do tamanho com `flight_takeoff_rounded` sobre `surfaceContainerHighest` e o avatar a 60 % de opacidade.
- `ring`: anel de 2 dp em `fill` com 2 dp de folga, usado na pessoa ativa do painel.
- `MemberAvatarStack(members, max: 3)`: sobreposição de −8 dp com contorno da superfície e "+N".
- `MemberChip`: avatar sm + nome (substitui os `Chip(avatar: CircleAvatar(color))`).

**C2 `StateView`** (DES-09): `StateView.loading(skeleton: SkeletonList(rows: 6))`, `StateView.empty(art: KubicoArt.basket, title, message, action: FilledButton)`, `StateView.error(error, onRetry)`, `StateView.offline()` e `StateView.noAccess()`.
- Título em `titleLarge`, mensagem em `bodyMedium` `onSurfaceVariant`, máx. 360 dp de largura, centrado, ilustração de 140 dp.
- O erro distingue rede (nuvem, "Sem ligação. Mostramos o que já tínhamos.") de servidor ("Algo correu mal do nosso lado.").
- O esqueleto são linhas `surfaceContainerHighest` com raio 8 e brilho a 1,2 s (estático com movimento reduzido).

**C3 `SectionHeader`**: título `titleLarge` + contador numa pílula (`labelMedium`, `surfaceContainerHighest`), ícone opcional da cor da área ou semântica (Em atraso: `warning`) e ação à direita (`TextButton`). 24 de espaço por cima, 8 por baixo.

**C4 `CheckCircleButton`** (concluir tarefa ou passo, DES-08)
- Alvo de 48 dp (56 em conforto/criança, 64 no painel), círculo de 28 dp com contorno de 2,5 dp `outline`.
- Ao tocar: enche-se de `success` em 150 ms, o ✓ desenha-se com `easeOutBack`, háptica média.
- Estados: disponível; feita (✓ preenchido); à espera do dia (relógio pequeno `outline` e o texto "volta a 12/11" visível na linha, não só na tooltip); atribuída a outra pessoa (sem círculo, mostra o avatar da pessoa no lugar); só ver (sem botão).
- `Semantics(button: true, checked: …, label: 'Marcar «Pôr a mesa» como feita')`.

**C5 `ChoreCard`**: cartão `surfaceContainerLow`, raio 16, padding 16.
- Linha 1: `CheckCircleButton` · título `titleMedium` · `PointsBadge`.
- Linha 2 (`bodyMedium`, `onSurfaceVariant`): ícone do período (☀/🌙 rounded) + recorrência + "com foto" em chips pequenos de ícone e texto, em vez de "·" encadeados.
- À direita, em baixo: `MemberAvatar.sm` do responsável, com "em rotação" como um ícone `sync_rounded` sobre o avatar.
- Rotina: passos como `CheckboxListTile` com alvos de 48, uma barra de progresso segmentada (um segmento por passo, cor `success`) por baixo do título e "2 de 5".
- Ao concluir: o cartão encolhe e sai (`SizeTransition` 250 ms) depois de 600 ms com o ✓ visível, e entra na secção "Feitas hoje" (recolhida) em vez de desaparecer.

**C6 `PointsBadge`**: pílula `tertiaryContainer` com `star_rounded` em `tertiary` e "5" em `labelMedium` tabular. Variante grande (`lg`) para o saldo: `displayMedium` + estrela de 40 dp. Dívida: pílula `warningContainer` "deve 5", nunca em vermelho de erro (não é um erro da criança).

**C7 `RewardCard`**: imagem ou emoji da recompensa (novo campo opcional `icon`, de uma lista fixa de ~24 desenhos: gelado, cinema, ecrã, bicicleta…) num quadrado de 56 dp `tertiaryContainer` · título · **barra de progresso** `saldo/custo` (`tertiary` sobre `surfaceContainerHighest`, 8 dp, raio 4) · "Faltam 12 ⭐" ou, se já dá, um botão `FilledButton` "Trocar" em `tertiary`. Nunca um botão desativado cinzento: quando ainda não chega, o botão não aparece, só a barra.

**C8 `Celebration`** (overlay, DES-07): ao concluir uma tarefa com pontos, a estrela voa do cartão até ao `PointsBadge` do cabeçalho (`Hero`-like com `OverlayEntry`, 400 ms) e o contador sobe a rolar (`TweenAnimationBuilder<int>`). Ao completar uma rotina ou conseguir uma recompensa, confettis (≤ 1,2 s, 40 partículas `CustomPainter` com as cores das áreas) e uma frase "Rotina da manhã feita! ⭐ +10". Anuncia-se com `SemanticsService.announce` e respeita o movimento reduzido. O SnackBar fica só para os adultos e para "Desfazer".

**C9 `QuickAddBar`** (Compras): campo pílula (raio 28, altura 56, `surfaceContainer`) com placeholder "Adicionar… (ex.: 2 leite)", 1 ícone de ação à direita que muda com o contexto (🎤 com o campo vazio, ➕ quando há texto) e um botão separado de 48 dp para o leitor de códigos. Fica fixa em baixo no telemóvel (acima do teclado, `e2`) e no topo do painel de detalhe nos ecrãs largos.

**C10 `ShoppingItemRow`**: caixa de seleção redonda de 24 dp em `secondary` (alvo 48), nome `bodyLarge`, quantidade como pílula à direita (`labelMedium` tabular, `surfaceContainerHighest`) e o avatar xs de quem pôs na lista, opcional. Marcado: riscado em `onSurfaceVariant` (não `outline`, DES-15), linha a 70 % e animação de 250 ms até à secção do carrinho. Deslizar para apagar mostra o SnackBar "«Leite» apagado · Desfazer" (DES-13).

**C11 `EventBlock`** (calendário): fundo `soft` do membro, barra lateral de 4 dp em `fill`, título `titleSmall` em `onSoft`, hora em `labelMedium` tabular, `MemberAvatarStack`. Conflito: contorno de 2 dp `warning` e um selo `error_rounded` com o texto do conflito numa segunda linha visível (não tooltip, DES-12/21). Boleia: chip `directions_car_rounded` "Leva: Rui".

**C12 `HouseholdSwitcher`**: no telemóvel, um botão pílula com o avatar da casa (símbolo ou inicial em `primaryContainer`) + nome + ▾, que abre uma `ModalBottomSheet` com as casas (cartões grandes com os membros em `MemberAvatarStack`) e "Adicionar casa". No grande, fica no topo do `NavigationDrawer`.

**C13 `OfflineBanner`** (DES-20): `MaterialBanner` em `warningContainer`, ícone `cloud_off_rounded` `onWarningContainer` e o texto "Sem ligação: podes continuar. 3 alterações por enviar." Ao enviar, passa a `infoContainer` com um progresso linear indeterminado. Ao terminar, mostra "Tudo enviado ✓" em `successContainer` durante 2 s e sai.

**C14 `PinPad`** (DES-17): 4–6 pontos de 16 dp, teclado 3×4 com teclas de 72 dp (`surfaceContainerHigh`, `headlineSmall`), ⌫ e háptica leve. Erro: abana 300 ms e os pontos ficam `error`. Bloqueio: um temporizador visível.

---

## 4. Redesenho dos ecrãs principais

Notação: as medidas são em dp; "→" indica a ação. A navegação passa a ter 5 destinos: **Hoje · Compras · Tarefas · Calendário · Recompensas**. A conta, os membros, o histórico, a ajuda e as sessões passam para um **menu de perfil** (avatar do utilizador no canto da barra) com secções e ícones; "Terminar sessão em todos" fica dentro de "Dispositivos" (DES-14).

### 4.1 Hoje (novo; ecrã inicial; corrige DES-04)

Objetivo: em 3 segundos, "o que é preciso fazer hoje e quem faz o quê".

**Telemóvel (360–599)**
```
┌──────────────────────────────────────┐
│ (🏠 Casa Silva ▾)          🔔  (A)   │  barra: switcher pílula · avisos · perfil
├──────────────────────────────────────┤
│ Bom dia, Ana                          │  headlineMedium (Nunito 28/700)
│ Domingo, 25 de outubro · 🌤 18°        │  bodyMedium onSurfaceVariant (tempo: futuro)
│                                       │
│ ┌─ Quem está em casa ────────────────┐│  linha de MemberAvatar md com nome por baixo
│ │ (A) (R) (M)  (✈B)                  ││  ausente com selo; toque → filtrar o dia
│ └────────────────────────────────────┘│
│                                       │
│ ┌ ⭐ Os teus pontos  120 ───── ▸ ┐     │  só criança/participante: cartão herói
│ │ ▓▓▓▓▓▓▓▓░░░ Gelado (150)       │     │  tertiaryContainer, raio 32
│ └────────────────────────────────┘     │
│                                       │
│ A seguir                         Ver ▸│  SectionHeader (cor Calendário)
│ ┃ 17:30  Natação · Maria  🚗 Rui      │  EventBlock compacto, máx. 3
│ ┃ 20:00  Jantar com avós              │
│                                       │
│ Tarefas de hoje (3)   ⚠ 1 em atraso ▸ │  SectionHeader (cor Tarefas)
│ ◯ Pôr a mesa              ⭐5   (M)   │  ChoreCard compacto (CheckCircle ativo)
│ ◯ Rotina da noite 2/5     ⭐10  (M)   │
│                                       │
│ Compras                          Ver ▸│
│ 🛒 Supermercado · 8 por comprar        │  cartão com barra de progresso 6/14
│                                       │
│ À espera de ti (2)                    │  só quem gere: aprovações (recompensas,
│ (M) Gelado · 150 ⭐   [Recusar][Aprovar]│  conclusões com foto)
└──────────────────────────────────────┘
│  🏠Hoje  🛒Compras  ✓Tarefas  📅Cal  ⭐  │  NavigationBar 72 dp, indicador por área
```
- O conteúdo é personalizado por papel: a criança vê primeiro "As minhas tarefas" e os pontos; o adulto vê primeiro "A seguir" e as aprovações. Cada bloco só aparece se a pessoa tiver acesso à área (`accessTo`).
- Cada bloco é um cartão `surfaceContainerLow` (raio 24) com o `SectionHeader` dentro. As ações principais fazem-se ali mesmo (concluir, aprovar).
- Os dados vêm das APIs que já existem (tarefas, calendário do dia, listas, recompensas). Se se quiser um só pedido, um `GET /households/{id}/today` agregado (decisão de engenharia).
- Estado vazio do dia: ilustração da casa com sol e "Dia tranquilo! Nada marcado para hoje.", com os botões "Nova tarefa" e "Novo evento" para quem gere.

**Tablet/PC (≥ 840)**: grelha de 2 colunas (60/40). À esquerda, "A seguir" (agenda do dia em linha temporal das 7 às 22 h com blocos) e "Tarefas de hoje". À direita, "Quem está em casa", "Pontos" (todas as crianças, cada uma com um anel de progresso para a sua próxima recompensa, **sem ranking**), "Compras" e "À espera de ti". Largura máxima de 1440.

### 4.2 Compras

**Telemóvel: listas**
- Cabeçalho do ecrã: "Compras" `headlineSmall`, com um ícone de cesto em `secondary`.
- Cada lista é um cartão de 72 dp: ícone escolhido (cesto, farmácia, bricolage, presente; campo `icon` opcional) num quadrado de 48 em `secondaryContainer` · nome `titleMedium` · "8 por comprar · 6 no carrinho" · um **anel de progresso** de 32 dp à direita.
- Apagar e renomear passam para um menu ⋮ por cartão ou para um toque longo, não para um caixote sempre visível (DES-13).
- FAB "Nova lista" (só quem gere).

**Telemóvel: artigos**
```
┌──────────────────────────────────────┐
│ ←  Supermercado            👥  ⋮      │  👥 = quem está a ver agora (futuro)
│ ▓▓▓▓▓▓░░░░░ 6 de 14                  │  progresso linear secondary, 4 dp
├──────────────────────────────────────┤
│ ⚠ Sem ligação: podes continuar. 2 …  │  OfflineBanner (warningContainer)
│                                       │
│ FRUTA E LEGUMES · 3                   │  SectionHeader sticky (titleSmall caps)
│ ◯ Bananas                       6     │  ShoppingItemRow (pílula de quantidade)
│ ◯ Tomate                     1 kg     │
│ LACTICÍNIOS · 2                       │
│ ◯ Leite                          2    │
│ ◯ Iogurtes                            │
│ SEM CATEGORIA · 1                     │
│ ◯ Pilhas AA                           │
│                                       │
│ No carrinho (6)  ▾        Limpar      │  recolhível; aberto por omissão
│ ✓ ~~Pão~~                             │
├──────────────────────────────────────┤
│ ( Adicionar… ex.: 2 leite      🎤 )[▦]│  QuickAddBar fixa em baixo (e2)
└──────────────────────────────────────┘
```
- Cabeçalhos de categoria `SliverPersistentHeader` fixos, porque a ordem por categoria já existe (DES-13). A categoria sai do subtítulo.
- Marcar um artigo: o círculo enche-se de `secondary` e, ao fim de 400 ms, a linha desliza para o carrinho (`AnimatedList` ou `SliverAnimatedList`; ver nota de desempenho). Háptica leve.
- Deslizar para apagar mostra o SnackBar com "Desfazer" (ou, melhor, o mesmo endpoint de desfazer do histórico).
- "Limpar carrinho" leva um diálogo de confirmação **só** se houver mais de 10 artigos; tem sempre desfazer.
- Quem só vê: sem `QuickAddBar` nem círculos, e com um aviso discreto no topo, "Só podes ver esta lista".

**Tablet/PC**: 2 painéis, com as listas a 360 dp (cartões compactos) e os artigos ao lado. A `QuickAddBar` vai no topo do painel de artigos e os artigos em 1 coluna até 720 dp. A partir de 1200, 2 colunas de categorias.

**Painel de cozinha**: ver 4.5.

**Nota de desempenho (fase 2):** manter o `ListView.builder`/slivers com as linhas preguiçosas. As animações de entrada e saída fazem-se só sobre a linha afetada (`AnimatedSize`/`SizeTransition` local com chave estável), sem reconstruir a lista.

### 4.3 Tarefas

**Telemóvel**
```
┌──────────────────────────────────────┐
│ Tarefas                      [Filtro▾]│  filtro: Todas · As minhas · por pessoa
│ (Todas) (A) (R) (M) (B)               │  MemberAvatar como chips de filtro (48 dp)
├──────────────────────────────────────┤
│ ⚠ Em atraso · 1                       │  SectionHeader em warning (não error)
│ ┌───────────────────────────────────┐ │
│ │ ◯  Regar as plantas      ⭐ 3      │ │  ChoreCard
│ │    ontem · semanal          (R)⟳  │ │  ⟳ = rotação
│ └───────────────────────────────────┘ │
│ Hoje · 3                              │
│ ┌───────────────────────────────────┐ │
│ │ ☀ Rotina da manhã        ⭐ 10     │ │  rotina: barra segmentada
│ │ ▰▰▱▱▱  2 de 5                (M)  │ │
│ │   ☑ Fazer a cama                   │ │  passos com alvo 48
│ │   ☑ Lavar os dentes                │ │
│ │   ☐ Vestir                         │ │
│ └───────────────────────────────────┘ │
│ ┌───────────────────────────────────┐ │
│ │ (R)  Despejar o lixo     ⭐ 5      │ │  atribuída a outra pessoa: avatar no
│ │      hoje · diária                │ │  lugar do círculo (sem botão desativado)
│ └───────────────────────────────────┘ │
│ Próximos 7 dias · 4             ▾     │  recolhível; "Mais tarde" e "Sem prazo" idem
│ Feitas hoje · 2                 ▸     │  recolhida; mostra o ✓ e quem fez
├──────────────────────────────────────┤
│                        [＋ Nova tarefa]│
```
- O cartão "Últimos 30 dias" sai do topo das tarefas e passa para "Quem fez o quê" (histórico) e para o ecrã Hoje. O formato muda de ranking para **progresso pessoal**: cada pessoa tem a sua barra de *tarefas feitas / atribuídas* e a sua sequência de dias, sem barras normalizadas pelo máximo da casa (DES-07). Os números ficam para os adultos.
- Para a criança (perfil de criança ou modo simples): só as suas tarefas, cartões de 88 dp com o título em `titleLarge`, o círculo de 64 dp e o ícone do período grande. Ao concluir, C8 (estrela que voa e contador). Ao completar a rotina, confettis e "Rotina da manhã feita!".
- Concluir uma tarefa com foto abre uma folha de baixo com a pré-visualização grande e o botão "Enviar e concluir". Enquanto envia, o círculo mostra o progresso.
- "Não aceite" (rejeitada): o cartão volta com uma faixa `warningContainer` "A mãe pediu para refazer", não em vermelho.

**Tablet/PC**: 2 painéis, com a lista à esquerda (máx. 560) e o detalhe ou editor da tarefa à direita (hoje o editor é um diálogo a partir de 700 dp). A partir de 1200, um quadro "por pessoa": uma coluna por membro com as tarefas de hoje e a última coluna "Qualquer pessoa", útil no painel.

### 4.4 Calendário

**Telemóvel**
- Barra compacta numa só linha: `‹  Outubro 2026  ›   [Hoje]  [Mês ▾]`. O seletor de vista (Agenda · Dia · Semana · Mês) vai num menu e o ícone de partilha passa para o ⋮. Poupa cerca de 50 dp (DES-12).
- **Vista por omissão: Agenda** (lista contínua de dias com eventos, "Hoje" fixo em cima). É a mais legível no telemóvel e para os avós.
- **Mês**:
  - células com altura mínima de 56 e `IntrinsicHeight` (sem 48 fixo, DES-06);
  - número em `labelLarge` tabular; o hoje vai num círculo preenchido `primary` com o número `onPrimary`; o selecionado num círculo `primaryContainer`; os dias fora do mês em `onSurfaceVariant` a 60 %, nunca `outline` como texto;
  - feriado: o número em `info` com um ponto `celebration` e o nome do feriado no cabeçalho do dia em `infoContainer` (já não `error`, DES-03);
  - até 3 marcadores de membro com "+N" em `labelSmall`;
  - toque → a agenda do dia por baixo, numa folha arrastável (`DraggableScrollableSheet`) que pode cobrir o mês.
- **Semana** (nova grelha de horas): 7 colunas, horas das 7 às 22 h (o resto recolhido), `EventBlock` posicionados e eventos de dia inteiro numa faixa em cima. No telemóvel mostra 3 dias com deslize horizontal.
- `EventBlock` como em C11. O conflito vê-se sempre (contorno `warning` + linha de texto).
- Ausências: uma faixa fina no topo do dia com o avatar e "fora".

**Tablet/PC (≥ 840)**: 2 painéis, com o mês à esquerda (min. 420, células de 72–96 com os títulos dos 2 primeiros eventos em chips coloridos `soft`) e a agenda do dia selecionado à direita. A partir de 1200, a semana em grelha de horas é a vista por omissão, com um filtro de membros por cima (chips com avatar que ligam e desligam as pessoas, com a contagem de eventos).

**Painel de cozinha**: ver 4.5 (agenda de hoje e amanhã, letra grande).

### 4.5 Painel de cozinha (novo; corrige DES-05)

Como se entra: em "Mais → Modo painel de cozinha" (só adultos com conta). A escolha fica guardada no dispositivo e pode ser a abertura por omissão nesse aparelho. Proteção: sair do modo painel ou abrir a administração pede o PIN de um adulto (ou toque longo de 2 s + palavra-passe). Ecrã sempre ligado com `wakelock_plus` (nova dependência; na web, Screen Wake Lock API). Orientação horizontal preferida.

**Layout de referência (1280×800, horizontal; grelha de 12 colunas, *gutter* 24, margem 40)**
```
┌──────────────────────────────────────────────────────────────────────────────┐
│  🏠 Casa Silva            DOMINGO, 25 OUT        18:42          🌤 18°   (⋯) │ 88 dp
│                                                   displayLarge  (tabular)    │
├───────────────────────────┬──────────────────────────┬───────────────────────┤
│ HOJE · AGENDA (col 1–5)   │ TAREFAS (col 6–9)        │ COMPRAS (col 10–12)   │
│                           │                          │                       │
│ ┃17:30 Natação            │  (A)  (R)  (M)  (B✈)     │ Supermercado  6/14    │
│ ┃      Maria · 🚗 Rui      │  quem és? (toca)         │ ◯ Bananas         6   │
│ ┃20:00 Jantar com avós    │ ┌──────────────────────┐ │ ◯ Leite           2   │
│ ┃      todos              │ │ ◯ Pôr a mesa   ⭐5 (M)│ │ ◯ Tomate        1kg   │
│                           │ │ ◯ Lixo         ⭐5 (R)│ │ ◯ Pilhas AA           │
│ AMANHÃ                    │ │ ☀ Rotina 2/5  ⭐10 (M)│ │ …                     │
│ ┃08:15 Escola  🚗 Ana      │ └──────────────────────┘ │ [ 🎤  Dizer o que     │
│ ┃18:00 Dentista · Rui     │                          │       falta        ]  │
├───────────────────────────┴──────────────────────────┴───────────────────────┤
│ ⭐ Maria 120 ▓▓▓▓▓░ Gelado    ⭐ Rui 45 ▓▓░░ Cinema     📣 "Jantar às 20h!"   │ 96 dp
└──────────────────────────────────────────────────────────────────────────────┘
```
- **Mosaicos**: cartões `surfaceContainerLowest` (branco) sobre `surface` creme, raio 32, padding 24, sombra `e1`. O cabeçalho de cada mosaico leva o ícone da área de 28 dp na cor da área, com `titleLarge` em maiúsculas pequenas e espaçamento de letras de +4 %.
- **Tipografia**: modo *Painel* (×1,35). O texto da linha fica a cerca de 23 sp e a hora a 57 sp. Feito para ler a 2–3 m: corpo ≥ 22 sp, nada abaixo de 17 sp.
- **Alvos de 64 dp** com 12 dp entre eles. Sem gestos escondidos (sem deslizar para apagar, sem toque longo para ações frequentes).
- **"Quem és?"**: tocar no avatar escolhe a pessoa ativa durante 2 minutos (anel `ring` + nome em cima). As conclusões e os artigos ficam em nome dela (usando o `DoneByUserId`, que já existe para quem gere; o painel funciona com a sessão de um adulto). As crianças podem ter PIN opcional para isto. Sem pessoa escolhida, concluir uma tarefa pergunta "Quem fez?" com avatares grandes (já há `_whoDidIt`, `chores_view.dart:153`).
- **Compras**: adicionar por voz (botão grande de 64 dp) e por leitor de códigos se houver câmara. Marcar está disponível; apagar listas e limpar o carrinho não (só na app normal).
- **Rodapé**: progresso de pontos de cada criança (anel e próxima recompensa, sem ranking) e um "recado" fixo (mensagem curta da família; fica como sugestão de funcionalidade, opcional).
- **Modo noite automático** (das 22h às 7h, ou com o sensor de luz se houver): fundo `#0F0B09`, só o relógio e o primeiro evento de amanhã, brilho a 30 % (`screen_brightness` opcional), sem animações. Um toque acorda o ecrã para o modo dia durante 1 minuto.
- **Retrato (tablet 800×1280)**: 2 linhas, com a Agenda em cima (largura total) e, em baixo, Tarefas e Compras lado a lado. O rodapé dos pontos mantém-se.
- **Atualização**: já vem em tempo real (SignalR). Ao perder a ligação, o mosaico mostra o `OfflineBanner` compacto e a hora da última atualização ("atualizado às 18:40"), nunca um ecrã de erro.
- **Burn-in** (ecrãs OLED ligados todo o dia): o relógio e o cabeçalho mudam de posição ±4 dp a cada 10 min.

---

## 5. Transversal

### 5.1 Acessibilidade visual (checklist para cada PR de UI)
1. Texto normal ≥ 4,5:1 e grande/ícones ≥ 3:1. O `outline` não serve para texto sobre `surfaceContainerHigh*`.
2. A cor nunca vai sozinha: membro com inicial ou emoji, estado com ícone e texto, conflito com texto visível.
3. Funciona com `textScaler` 2,0 sem cortar: sem `SizedBox(height/width)` fixos em contentores de texto, sem `FittedBox` para encolher texto, `Wrap` em vez de `Row` para metadados.
4. Alvos ≥ 48 dp (56 no conforto e na criança, 64 no painel) e `materialTapTargetSize.padded`.
5. Tooltips só como complemento: o que importa vai em texto ou em `Semantics`.
6. Animações com `MediaQuery.disableAnimationsOf`.
7. Teste de contraste automático: um `test/design_tokens_test.dart` que percorre os pares (como o cálculo deste relatório) e falha abaixo do limite, para os tokens claro, escuro e membros.

### 5.2 Modo escuro
- Tokens próprios (2.2) e membros na tonalidade "clara" com iniciais escuras (2.3).
- Os QR (`household_screen.dart:592-594`, `two_factor_section.dart:195-197`) mantêm o fundo branco (necessário para a leitura), dentro de um cartão com raio 16 e margem branca de 16 dp.
- As fotos de prova levam uma moldura `outlineVariant` de 1 dp (no escuro, as fotos claras "estouram").

### 5.3 Modo simples (avós e crianças, DES-16)
- Ativa-se por pessoa (no perfil de criança, por omissão) ou por dispositivo.
- Efeito: modo de leitura *Conforto*, navegação reduzida às áreas usadas (por exemplo, criança: Hoje · Tarefas · Recompensas), ícones sempre com rótulo, sem menus ⋮ (as ações aparecem como botões com texto), confirmações para o que é destrutivo e um só FAB por ecrã.
- No login de criança: grelha de avatares grandes (88 dp, nome em `titleMedium`) e `PinPad` (C14).

### 5.4 Micro-interações (resumo)
| Momento | Animação | Háptica |
|---|---|---|
| Marcar artigo | círculo enche-se em 150 ms, a linha vai para o carrinho em 250 ms | leve |
| Concluir tarefa | ✓ com `easeOutBack`, estrela voa até ao saldo em 400 ms, contador rola | média |
| Completar rotina | confettis ≤ 1,2 s + frase | média |
| Trocar recompensa | o cartão "abre" (escala 0,96 → 1) e o saldo desce a rolar | forte |
| Mudar de separador | `FadeThrough` 250 ms | — |
| Mudar a vista do calendário | deslize horizontal ao navegar ‹ › | — |
| Tempo real (alguém mudou) | fundo `primaryContainer` a 30 % que se desvanece em 1 s na linha alterada | — |
| Sem ligação → enviado | o banner muda de cor e sai | — |

---

## 6. Plano de implementação (sugestão de ordem)

1. **Fundações** (1–2 dias): `design/tokens.dart`, `KubicoColors`, `buildTheme` explícito, fontes como assets, `VisualDensity.standard`, `test/design_tokens_test.dart` (contraste). `colorFromHex` passa a `KubicoColors.member(keyOrHex)` com o mapa dos hex antigos. Ícones `_rounded`.
2. **Componentes** (2–3 dias): `MemberAvatar`/`MemberChip`/`MemberAvatarStack` (substituir as 10 cópias), `StateView` + esqueletos, `SectionHeader`, `CheckCircleButton`, `PointsBadge`, `OfflineBanner`. Golden tests de cada componente em claro, escuro e com `textScaler` 2,0 (com as fontes reais carregadas no `flutter_test_config.dart` via `FontLoader`).
3. **Marca** (1 dia + designer): símbolo, ícones das plataformas, manifesto, ecrãs de arranque, página de confirmação do sync e `index.html`.
4. **Ecrãs**: Tarefas e Recompensas (gamificação: C5–C8) → Compras (C9–C10, categorias, desfazer) → Calendário (barra, mês, agenda, depois a semana em grelha) → **Hoje** → **Painel de cozinha** (inclui `wakelock_plus` e "quem és?").
5. **Servidor** (pequeno): chave de cor do membro (com o mapeamento dos hex) e `NextColorAsync` com as 11 chaves; campos opcionais `avatar` (membro), `icon` (recompensa, lista). Cada um com testes de integração e com as traduções em `Texts.cs`/`strings_en.dart`.

Os textos novos ("Bom dia, {0}", "Dia tranquilo!", "Quem és?", "Faltam {0} ⭐", "Feitas hoje"…) seguem a regra `.tr` + `strings_en.dart`.

---

## Top 5 (o que mais vale a pena fazer)

1. **Tokens próprios + paleta de membros acessível** (DES-02, DES-03, DES-15): substituir o `fromSeed` pelo `ColorScheme` explícito, criar a `KubicoColors` (sucesso, aviso, informação, áreas) e as 11 cores de membro com pares tonais, todas ≥ 4,5:1. Corrige o maior problema de acessibilidade e devolve a terracota à marca. Um teste automático de contraste impede regressões.
2. **Marca nas plataformas** (DES-01): símbolo "telhado-K", ícones da app, da web e do Android, manifesto, ecrãs de arranque e a página de ligação do calendário. É barato e é o que mais mexe na confiança à primeira vista.
3. **Ecrã "Hoje" como início** (DES-04): resumo por papel com a agenda, as tarefas, as compras e as aprovações, com ações ali mesmo. É também a base do painel de cozinha.
4. **Componentes partilhados e gamificação visual** (DES-07, DES-08, DES-10, DES-09): `MemberAvatar`, `CheckCircleButton`, `PointsBadge`, `RewardCard` com progresso, celebração e `StateView`. O resumo de pontos passa de ranking a progresso pessoal.
5. **Painel de cozinha + robustez com texto grande** (DES-05, DES-06, DES-16): layout de mosaicos com ecrã sempre ligado, "quem és?", modo noite e alvos de 64 dp. Em paralelo, tirar as alturas e larguras fixas e o `FittedBox`, usar densidade padrão e acrescentar o modo *Conforto*.
