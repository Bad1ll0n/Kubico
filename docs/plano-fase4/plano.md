# Kubico — Fase 4: de 7/10 a 10/10

Plano para os 27 pontos da avaliação de 03/11/2026 (features, visual, desempenho e automatismos). Cada ponto tem
um id (`F4-xx`), o que muda no backend e na app, os testes que o provam e o tamanho:

- **P**: uma sessão;
- **M**: duas a três sessões;
- **G**: uma semana de sessões.

As regras do `CLAUDE.md` valem para tudo:
- permissões por área e isolamento entre casas;
- textos em pt-PT com tradução para inglês;
- testes de integração no backend e testes na app;
- concorrência provada em SQLite e em PostgreSQL;
- letra a 200 % e "reduzir movimento" respeitados;
- um commit por bloco, e push só no fim, quando pedido.

## Estado (atualizado no fim da sessão)

- **Feito:**
  - 4B (F4-14, F4-17 a F4-20);
  - 4C (F4-11, F4-21, F4-26);
  - 4D (F4-09, F4-22);
  - 4E (F4-02, F4-04, F4-05, F4-06; F4-03 só com emoji);
  - 4F (F4-07, F4-12, F4-13);
  - 4G (F4-08);
  - 4H (F4-10);
  - 4I (F4-23);
  - 4A (F4-01: verde-salva e creme, cubo isométrico);
  - 4J (F4-24: assistente com o Claude — frase, talão, PDF da escola; à espera de uma chave da API).
  - F4-03 (fotografia de perfil, só adultos), F4-04 (arrastar para reordenar), PDF da Casa dentro da app e F4-16c
    (casa inteligente com o Home Assistant).
- **À espera de decisões:** 4K (D2, D3, D5).

## Decisões que só tu podes tomar

| Decisão | Bloqueia | Opções |
|---|---|---|
| D1. Direção da marca | 4A | A (cubo, azul-petróleo e coral), B (casa com sol, índigo e amarelo), C (blocos da família, verde-salva e areia) |
| D2. Fornecedor de email | 4K (recuperar palavra-passe, verificar email, consentimento parental, resumo semanal por email) | Brevo (gratuito até 300/dia, UE), Azure Communication Services, SendGrid |
| D3. Alojamento | 4K (publicação automática, cópias fora do servidor, alertas), F4-25 (sync na hora) | VPS na UE (Hetzner, OVH) com Docker Compose, ou Azure (a especificação sugere-o) |
| D4. Fornecedor de IA | 4J | API da Anthropic (Claude), com limites por casa e sem dados de crianças nos pedidos |
| D5. Conta Apple Developer | F4-16 (avisos no iPhone), widgets iOS | 99 USD/ano |

Até haver resposta, avançam os blocos que não dependem de nenhuma: 4B, 4C, 4D, 4E, 4F, 4G, 4H e 4I.

## Ordem proposta

| Bloco | Tema | Pontos | Tamanho | Depende de |
|---|---|---|---|---|
| 4A | Marca nova | 1 | M | D1 |
| 4B | Rápida, sem Google e sem rede | 14, 17–20 | G | — |
| 4C | Lembretes e resumos | 11, 21, 26 | M | — |
| 4D | Compras inteligentes e refeições | 9, 22 | G | — |
| 4E | Visual e interação | 2–6 | G | 4A (para não repetir trabalho de cor) |
| 4F | A casa: equipamentos, documentos, recados | 7, 12, 13 | G | 4C (avisos de prazo) |
| 4G | Portugal: IUC, IPO, IRS, seguros | 8 | M | 4C, 4F (carro como equipamento) |
| 4H | Despesas partilhadas | 10 | M | — |
| 4I | Tarefas justas | 23 | P | — |
| 4J | IA em linguagem natural | 24 | G | D4, 4D, 4H |
| 4K | Conta, operação e plataformas | 15, 16, 25, 27 | G | D2, D3, D5 |

---

## 4A — Marca nova (D1)

**F4-01. Aplicar a direção escolhida** (M)
- `design/tokens.dart`:
  - nova `KubicoPalette` clara e escura;
  - cores das áreas;
  - tons de aviso, sucesso e informação revistos para combinar.
  O `design_tokens_test.dart` verifica o contraste de cada par (≥ 4,5:1 no texto, ≥ 3:1 nos gráficos).
- `design/brand.dart`: novo `KubicoMarkPainter`, e daí os ícones de todas as plataformas (`tool/brand_icons_test.dart`):
  - Android adaptativo;
  - iOS;
  - web e favicon;
  - Windows, macOS e Linux.
- `web/index.html` e `manifest.json`: as cores do ecrã de carregamento e o `theme_color` passam a vir dos tokens. Um teste compara-os, para não voltarem a divergir (DES-22).
- `illustrations.dart`: o papel e os tons seguem a nova paleta.
- A página de confirmação da sincronização dos calendários (`CalendarSyncEndpoints`) segue a nova cor.
- **Feito quando**: não sobra nenhuma cor da paleta terracota (procura por hex no repositório), os testes de contraste passam e há capturas de ecrã antes e depois em claro e escuro.

## 4B — Rápida, sem Google e sem rede

**F4-18. Motor gráfico e fontes servidos pela própria app** (P)
- `flutter build web --no-web-resources-cdn`: o CanvasKit e o skwasm vão com a app, não do `gstatic.com`.
- A fonte de recurso deixa de ser o Roboto do Google Fonts. Os temas usam `fontFamilyFallback` com as fontes da app, e mais nenhuma fonte se descarrega de fora.
- A CSP perde `https://www.gstatic.com` no `script-src` (o Firebase JS passa também a ser servido localmente) e o `fonts.gstatic.com`.
- **Feito quando**: uma visita a frio não faz pedidos a domínios da Google antes de a pessoa entrar. Prova-se com um teste no browser que lista os pedidos.

**F4-17. WebAssembly e carregamento diferido** (M)
- Compilar com `--wasm` (skwasm), mantendo o JS para browsers sem WasmGC.
- `deferred as` para o que é raro:
  - painel de cozinha;
  - leitor de códigos;
  - sincronização dos calendários;
  - a minha conta e 2FA;
  - histórico;
  - ajuda.
- O CI mede o tamanho do `main.dart.js` (comprimido) e falha se passar o limite (hoje 1,15 MB; meta < 800 KB).

**F4-19. Um só pedido para o ecrã Hoje** (P)
- `GET /api/households/{id}/today` devolve, de uma vez:
  - a casa;
  - as ausências;
  - as tarefas de hoje e em atraso;
  - os eventos de hoje e de amanhã;
  - a lista com mais por comprar;
  - os pontos e as aprovações pendentes.
- Respeita as permissões de cada área: o que não se vê vem a null.
- Testes: as permissões por papel e o isolamento entre casas. O teste de desempenho conta as consultas (≤ 8).

**F4-14. Sem rede em todas as áreas** (G)
- Cópia local em memória, gravada 1 s depois como nas Compras, para:
  - Hoje;
  - Tarefas;
  - Calendário (o intervalo à vista e o seguinte);
  - Recompensas;
  - a casa e os membros.
- O ecrã abre logo com a cópia e atualiza-se a seguir (*stale-while-revalidate*), com o aviso "sem ligação" na cor de aviso.
- Fila para concluir tarefas e passos de rotinas sem rede, com o mesmo modelo da fila das compras:
  - id gerado na app;
  - repete as falhas que passam;
  - `rejections` para o que o servidor recusa de vez, por exemplo um 409 "já está feita".
- **Feito quando**: em modo avião abrem-se todas as áreas com o último estado, concluir uma tarefa fica na fila, e ao voltar a rede a tarefa vai e os pontos aparecem.

**F4-20. Arranque a frio da API** (P)
- `PublishReadyToRun` e `TieredPGO` no `Dockerfile`, e modelo compilado do EF (`dotnet ef dbcontext optimize`, com um teste que falha se o modelo ficar desatualizado).
- Uma consulta de aquecimento no arranque, antes do `/health/ready`.
- **Feito quando**: o primeiro `/api/me` depois de arrancar fica abaixo de 80 ms. Hoje são 325 ms.

## 4C — Lembretes e resumos

**F4-11. Lembretes por hora** (M)
- Eventos:
  - `Reminders` (lista de minutos antes: 10, 30, 60, 1 dia), por evento, com um valor por omissão por pessoa;
  - nas séries, vale para cada ocorrência;
  - com sync ao Google, Outlook e iCloud (VALARM no ICS e nos fornecedores).
- Tarefas: `RemindAt` (hora do dia, no fuso da casa). O lembrete vai só a quem a tem atribuída e só se ainda não estiver feita.
- `ReminderWorker`: de minuto a minuto, procura o que vence nos próximos minutos, por índice. Usa `NotificationService.NotifyAsync` com novos `NotificationKind` (`EventReminder`, `ChoreReminder`) e respeita as horas de silêncio.
- Índice único (pessoa, coisa, ocorrência, minutos) para não enviar duas vezes, também com várias instâncias (ver P18).
- **Testes**:
  - o `FakeTimeProvider` avança o relógio;
  - uma série semanal dá um lembrete por ocorrência;
  - a mudança de hora não duplica nem salta;
  - quem perdeu o acesso ao calendário não recebe;
  - não há duplicados em pedidos paralelos.

**F4-21. Resumo da manhã e resumo semanal** (M)
- Resumo da manhã, por pessoa, à hora escolhida (por omissão 7h30 no fuso da casa): "Hoje: natação às 17h, 3 tarefas, falta leite".
  - Só leva o que a pessoa vê.
  - Não vai se não houver nada.
  - Liga-se e desliga-se nas preferências dos avisos.
- Resumo semanal para quem gere as Tarefas (domingo às 19h): tarefas feitas e em atraso por pessoa, pontos ganhos e pedidos à espera.
  - Na app.
  - Por email quando houver D2.
- Testes: o conteúdo por papel (uma criança não vê o de outra), o fuso e a mudança de hora, e as preferências desligadas.

**F4-26. Regras automáticas** (M)
- Recompensas: "aprovar sozinho até N pontos", por casa (Recompensas, gerir). Passa pelo `Points.LockAsync`, e fica no histórico como "aprovado automaticamente".
- Mesada: lembrete ao adulto no dia de pagar (fim da semana ou do mês) se houver períodos por pagar.
- Doença ou ausência curta: ao marcar a ausência de hoje, as tarefas de hoje passam para a próxima pessoa da rotação. Hoje só se tira a pessoa das rotações seguintes.
- Testes de concorrência na aprovação automática (dois pedidos ao mesmo tempo não passam do saldo).

## 4D — Compras inteligentes e refeições

**F4-22a. Artigos recorrentes** (M)
- `RecurringItem` por lista: nome, quantidade e regra (semanal num dia, ou a cada N dias).
- Um worker põe o artigo na lista no dia, se ainda não lá estiver por comprar (sem duplicados, com índice único).
- Na app: "Repetir" no menu de um artigo, e uma lista de recorrentes no menu da lista.

**F4-22b. Sugestões a partir do histórico** (M)
- `PurchaseHistory`: quando se limpa o carrinho, guarda-se o que se comprou e quando, por casa.
- `GET /lists/{id}/suggestions` devolve o que costuma acabar (intervalo médio entre compras ≥ 2 registos) e já passou do prazo. Na app, uma faixa "Costumas comprar" com um toque para pôr.
- Ao escrever um artigo, o autocompletar usa os nomes da casa (`HouseholdProduct`) antes de mais nada.
- Também entra na exportação RGPD.

**F4-22c. Ordem do supermercado** (P)
- Guarda-se a ordem em que se marcam os artigos, por categoria e por lista. A lista ordena as categorias pela ordem habitual, em vez da fixa.
- Há sempre a opção de voltar à ordem por omissão.

**F4-09. Refeições e receitas** (G)
- Área nova "Refeições", com permissões por papel como as outras (`HouseholdModule.Meals`, `ModulePermissions`, grupo SignalR próprio):
  - `Recipe`: nome, porções, ingredientes (nome, quantidade, unidade, categoria), passos, fotografia opcional e etiquetas;
  - `MealPlan`: dia, refeição (almoço ou jantar) e receita ou texto livre.
- "Pôr os ingredientes na lista": junta as quantidades da semana, salta o que já está na lista e pergunta pelo que há em casa.
- Ecrã de semana (7 dias × 2 refeições) e lista de receitas com pesquisa.
- Modelos ao criar a casa: 10 receitas portuguesas de base.
- Histórico e desfazer para receitas e plano (`ActivityLog.Capture`), exportação RGPD, testes de permissões e isolamento.

## 4E — Visual e interação (depois de 4A)

**F4-02. Ecrã largo em todas as áreas** (M)
- Tarefas, Recompensas e Compras com `maxWidth: 720` e painel de detalhe a partir de 840 px (lista à esquerda, detalhe à direita), como o calendário.
- Hoje em duas colunas a partir de 840 px.
- Teste por área: a 1280 px nada fica esticado a toda a largura, e a 200 % nada corta.

**F4-03. Fotografia ou emoji no avatar** (M)
- `User.AvatarPhotoId` (a mesma regra das fotos de prova: tira os metadados, ≤ 1024 px, 1 por pessoa) ou `AvatarEmoji`. As crianças escolhem entre emojis (sem fotografia de crianças por omissão; os pais podem permitir).
- O `MemberAvatar` mostra a fotografia ou o emoji com o anel na cor da pessoa (a cor nunca vai sozinha).
- Exportação RGPD e limpeza ao apagar a conta.

**F4-04. Gestos** (P)
- Deslizar para a direita marca o artigo ou conclui a tarefa (com Desfazer); para a esquerda, apaga, com confirmação nas tarefas.
- Arrastar para reordenar as listas e os artigos dentro de uma categoria.
- Cada gesto tem sempre um botão alternativo (acessibilidade) e uma ação no leitor de ecrã (`CustomSemanticsAction`).

**F4-05. Animações de conclusão** (P)
- O artigo marcado desliza para o carrinho, com `AnimatedList`.
- A tarefa feita risca-se e encolhe antes de sair.
- A barra de progresso das recompensas anima-se.
- Sem nada disto com `MediaQuery.disableAnimationsOf`.

**F4-06. Primeiro uso guiado** (M)
- Depois de criar a casa, 3 passos: convidar a família (QR), criar o primeiro perfil de criança (se o modelo tiver crianças) e pôr a app no ecrã inicial ou instalar a PWA.
- Dicas de primeira vez em cada área (uma vez por aparelho e por pessoa).
- **Meta**: da instalação à primeira lista partilhada em menos de 2 minutos, medido num teste de ponta a ponta.

## 4F — A casa: equipamentos, documentos e recados

**F4-07. Equipamentos e garantias** (G)
- Área nova "Casa" (`HouseholdModule.Home`):
  - `Appliance`: nome, divisão, marca e modelo, data de compra, fim da garantia, fatura e manual (ficheiros);
  - `MaintenanceTask`: periodicidade, última vez e próxima vez, por exemplo "Limpar o filtro do exaustor, a cada 3 meses".
- Ficheiros: PDF e imagens até 10 MB, guardados como as fotos (verificados, sem metadados), com quota por casa.
- Avisos (F4-11): a garantia acaba em 30 dias, e a manutenção vence (pode gerar uma tarefa atribuída).
- Modelos: caldeira, ar condicionado, máquina da loiça, carro (para 4G).

**F4-13. Cofre de documentos** (M)
- `Document`: tipo (cartão de cidadão, passaporte, carta de condução, boletim de vacinas, seguro), pessoa, número e validade.
- O número e os ficheiros ficam cifrados com o Data Protection e só os veem a própria pessoa e quem gere a área. As crianças não veem os dos outros.
- Aviso 60 e 30 dias antes da validade.
- Pede a sessão ativa (`.RequireActiveSession()`) para ver os ficheiros. Fica no histórico sem os números e entra na exportação RGPD.

**F4-12. Quadro de recados, contactos e aniversários** (M)
- Recados: notas curtas no Hoje e no painel de cozinha, com validade opcional. Em tempo real.
- Contactos úteis: nome, papel (médico, escola, canalizador), telefone e notas, com toque para ligar.
- Aniversários: data por membro (adultos, opcional) e por contacto. Entram no calendário como eventos de dia inteiro, com o aviso da véspera.

## 4G — Portugal

**F4-08. IUC, inspeção (IPO), IRS e seguros** (M)
- Carro (equipamento do tipo veículo): matrícula, data da primeira matrícula e categoria. A partir daí, sozinhos:
  - **IUC**: o mês da matrícula, com aviso no início do mês;
  - **IPO**: 4 anos, depois 2 em 2 e depois anual a partir dos 8 anos (ligeiros), com aviso 30 dias antes.
- **IRS**: o período de entrega (1 de abril a 30 de junho) e lembretes para validar faturas no e-Fatura (fevereiro), configuráveis.
- Seguros: data de renovação por apólice, com aviso.
- As regras ficam numa tabela no servidor (`PortugalCalendar`), com testes por ano. Os textos em inglês explicam o que é cada um.

## 4H — Despesas partilhadas

**F4-10. Quem pagou o quê** (M)
- Área nova "Despesas" (`HouseholdModule.Expenses`):
  - `Expense`: valor em cêntimos, quem pagou, entre quem se divide (igual, por partes ou por valores), categoria, data e fotografia do talão;
  - `Settlement` regista os acertos.
- Saldos: quem deve a quem, com o mínimo de transferências. Calcula-se no servidor e prova-se com testes de propriedades (a soma dá sempre zero).
- Ligação às compras: ao limpar o carrinho, "registar como despesa" com o total.
- Concorrência (dois acertos ao mesmo tempo), histórico e desfazer, exportação RGPD.

## 4I — Tarefas justas

**F4-23. Distribuição pela carga** (P)
- Novo modo de atribuição, "a quem tem menos": escolhe a pessoa da rotação com menos pontos (ou menos tarefas) nos últimos 14 dias, saltando quem está ausente.
- O ecrã de estatísticas mostra a carga por pessoa sem *ranking* entre irmãos (DES-07): cada um vê o seu progresso, e os pais veem o equilíbrio.
- Testes: empates, ausências e concorrência na escolha.

## 4J — IA em linguagem natural (D4)

**F4-24a. Pedir por frase** (M)
- `POST /households/{id}/assistant`: o texto ("põe ovos e leite e marca o dentista terça às 10") passa a uma lista de ações tipadas (pôr artigo, criar evento, criar tarefa, criar despesa).
- A app mostra as ações para confirmar antes de fazer. Cada uma passa pelos endpoints normais, com as permissões de quem pede.
- Sem dados das crianças no pedido ao modelo. Limite de pedidos por casa e por dia. Desliga-se por casa. Nos testes há um `FakeAssistant`.

**F4-24b. Talão e PDF da escola** (M)
- Fotografia do talão: risca os artigos da lista e propõe a despesa (4H).
- PDF do calendário escolar: propõe os eventos (dias sem aulas, reuniões), para confirmar.
- Os ficheiros não ficam guardados depois de lidos, salvo se a pessoa os juntar à despesa.

## 4K — Conta, operação e plataformas

**F4-16a. Recuperar a palavra-passe e verificar o email** (D2, M)
- Link de uso único (30 min), com limites por IP e por email. Não diz se o email existe.
- Verificar o email ao registar: sem verificação, não se convida nem se recupera.
- Consentimento parental: quem cria um perfil de criança confirma que é o responsável, com registo da data.

**F4-27. Operação** (D3, G)
- Publicação automática pelo CI (imagem para um registo, deploy com `docker compose pull` e migrações) com ambiente de testes antes de produção.
- Cópias de segurança fora do servidor (cifradas, diárias, com retenção de 30 dias) e um teste de reposição mensal automático.
- Erros com o Sentry (app e API, sem dados pessoais) e alertas de disponibilidade.
- Dependabot e uma auditoria às dependências no CI.

**F4-25. Calendário sincronizado na hora** (D3, M)
- Notificações push do Google (`watch`) e subscrições do Microsoft Graph, renovadas antes de expirar.
- O worker de 5 minutos fica só como rede de segurança.

**F4-16b. Avisos no iPhone** (D5, P): a chave APNs no Firebase e a configuração da app iOS.

**F4-15. Widgets e atalhos de voz** (M; o iOS depende de D5)
- Android: widget "Hoje" e widget "Compras" (`home_widget`), atualizados por um push silencioso.
- iOS: os mesmos, com WidgetKit.
- Atalhos: "Põe leite na lista do Kubico", com App Intents (iOS) e App Actions (Android).

**F4-16c. Casa inteligente** (depois do resto, G): integração com o Home Assistant (token de longa duração), com eventos que geram tarefas ("a máquina de lavar acabou: estender a roupa").

---

## Como se vai medir o 10/10

- **Desempenho**:
  - primeira visita à web < 2,5 s em 4G simulado;
  - `main.dart.js` < 800 KB comprimido;
  - Hoje com um só pedido;
  - API < 80 ms a frio.
- **Sem rede**: todas as áreas abrem em modo avião, e as alterações às tarefas e às compras chegam quando a rede volta.
- **Automatismos**: a app avisa sem ninguém pedir (lembretes, resumo da manhã, prazos da casa e de Portugal, recorrentes nas compras).
- **Acessibilidade**: letra a 200 %, leitor de ecrã e "reduzir movimento" em todos os ecrãs novos, com testes.
- **Privacidade**:
  - nenhum pedido à Google antes de entrar;
  - documentos e números cifrados;
  - IA sem dados de crianças;
  - exportação RGPD com tudo o que é novo.
