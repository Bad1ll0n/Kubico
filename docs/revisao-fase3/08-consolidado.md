# Kubico — Revisão fase 3: relatório consolidado

Branch `mvp-continuacao` (60bb5a1). Sete revisores seniores, 144 itens no total. Os relatórios de cada área estão nesta pasta (01 a 07). Aqui juntam-se os repetidos e propõe-se a ordem.

| Área | Ficheiro | Crítico | Alto | Médio | Baixo |
|---|---|---|---|---|---|
| Engenharia backend | 01 | 0 | 6 | 13 | 9 |
| Engenharia Flutter | 02 | 1 | 3 | 9 | 3 |
| QA | 03 | 0 | 2 | 7 | 7 |
| Segurança | 04 | 0 | 4 | 8 | 8 |
| DevOps/SRE | 05 | 1 | 6 | 9 | 4 |
| Produto/UX | 06 | 4 | 7 | 8 | 2 |
| Design visual | 07 | — | 23 itens + design system e redesenho de 5 ecrãs |

Veredicto comum: o núcleo técnico está sólido (isolamento entre casas aguentou, concorrência certa em PostgreSQL, fusos e 29/02 bem no servidor), mas a app ainda não está pronta para famílias reais: pode perder dados, não protege bem os dados das crianças, não tem recuperação de conta nem cópias de segurança, e não tem cara própria nem ecrã "Hoje".

## Problemas encontrados por vários revisores (juntos)

| Tema | Itens | Gravidade |
|---|---|---|
| Fila sem rede das compras perde alterações (401 no arranque, 5xx, 408, 429) | APP-01, QA-02 | Crítico |
| Sem cópias de segurança nem plano para a chave-mestra | OPS-01, OPS-12 | Crítico |
| Sem recuperar/mudar palavra-passe nem verificar email; admin de suporte decidido pelo email | UX-01, SEC-04, SEC-03 | Crítico |
| Sem aviso de privacidade nem consentimento parental | UX-04 | Crítico |
| Dados das crianças: feed ICS sobrevive a PIN/código/logout; PIN adivinha-se e bloqueia-se; fotos visíveis a convidados; ficam 90 dias | SEC-01, SEC-02, QA-16, SEC-08, SEC-10 | Alto |
| Erros 500: prazo no ano 1 parte Tarefas da casa; `PauseFor` transborda; violações de índice único | QA-01, QA-05, ENG-04 | Alto |
| Pontos: apagar tarefa apaga pontos já trocados; "não aceitar" faz recuar; modelo de família com rotinas partilhadas entre irmãos | ENG-01, ENG-03, ENG-15, UX-03 | Alto |
| Desfazer: `UpdatedAt` antigo ignorado pelos outros aparelhos; repõe referências mortas; não há "Desfazer" no snackbar | ENG-02, ENG-06, UX-06 | Alto |
| CI só em SQLite: migrações e concorrência nunca testadas em PostgreSQL | ENG-05, OPS-06, QA-08 | Alto |
| App frágil na rede: sessão perde-se num soluço, SignalR desiste, HTTP sem timeout | APP-02, APP-03, APP-04 | Alto |
| Operação: sem restart/healthcheck, sem observabilidade nem alertas, avisos com reenvios e abortos, proxy = um só IP | OPS-02..05, ENG-20, OPS-07 | Alto |
| Fotos: sem quota, PNG gigante aceite, bytes a mais guardados | SEC-07, ENG-19, QA-09 | Médio |
| Datas na app: T1, editor do calendário perde um dia na mudança de hora, "hoje" no fuso do telemóvel, testes com bombas-relógio, CI em UTC | QA-03/04/06/07/11, APP-12 | Médio |
| Aviso EF Skip/Take sem OrderBy (`NotificationDispatcher.cs:43-50`) | ENG-07, OPS-18, QA-15 | Médio |
| Ecrã "Hoje" em falta (todos abrem em Compras; criança vê tudo) | UX-02, DES-04 | Crítico (produto) |
| Sem marca (ícones e azul do Flutter), cores de membro sem contraste (até 1,52:1), texto grande corta | DES-01/02, UX-09 | Alto |
| Painel de cozinha, modo simples para avós, trocar de criança num tablet | DES-05, DES-16, UX-08, UX-11 | Alto |
| Convite sem partilha nativa/deep link; tocar num aviso não abre nada | UX-05, UX-07, APP-11 | Alto |
| Android release assinado com a chave de debug, allowBackup ligado | APP-10 | Médio (bloqueia publicar) |

## Plano proposto (fases)

**3A — Não perder dados nem expor crianças** (bloqueadores, só código; sem decisões pendentes)
1. Fila sem rede: repetir 401/5xx/408/429, esperar pela sessão no arranque, só descartar 4xx definitivos com aviso (APP-01, QA-02, APP-06).
2. Dados das crianças: anular feeds ICS ao mudar PIN/código/terminar sessões; PIN com escalada e aviso aos pais, bloqueio que não se usa contra a criança; fotos só para quem gere e para a própria; apagar ao tirar o perfil (SEC-01/02/08/10, QA-16).
3. Validação e "nunca 500": intervalos de datas nas tarefas e ausências, violações de índice → 409 (QA-01, QA-05, ENG-04).
4. Pontos e desfazer: confirmação de pontos trocados ao apagar; "não aceitar" sem recuar a tarefa; desfazer com `UpdatedAt` novo e verificação das referências; rotinas por criança no modelo (ENG-01/02/03/06/15, UX-03).
5. Fotos: quota por casa, limite de píxeis, regravar a imagem (SEC-07, QA-09, ENG-19).
6. Datas: corrigir T1 e o editor do calendário, relógio injetável na app, CI também em Europe/Lisbon, tirar as bombas-relógio dos testes; corrigir o aviso EF (QA-03/04/06/07, ENG-07).

**3B — Conta e confiança** (precisa de decisões: fornecedor de email)
Mudar e recuperar palavra-passe, verificar email, admin de suporte por configuração, aviso de privacidade e consentimento parental, login que não se bloqueia só com o email, limites nos códigos de convite (UX-01/04, SEC-03/04/05/06).

**3C — Robustez e operação** (precisa de decisão: onde alojar)
App resiliente (sessão, SignalR, timeouts), PostgreSQL no CI com migrações, restart + `/health/live` e `/ready`, logs estruturados e métricas/alertas, envio de avisos robusto, `KnownNetworks`, utilizador da base sem superpoderes, compose completo, cópias de segurança e cofre da chave-mestra, assinatura Android (APP-02/03/04/10, OPS-01..10).

**3D — Design e experiência**
Design system (tokens, cores de membro acessíveis com teste de contraste, Nunito + Atkinson Hyperlegible), marca em todas as plataformas, ecrã "Hoje", componentes partilhados e gamificação para crianças, convite partilhável com deep link, avisos que abrem o sítio certo, acessibilidade e texto grande, painel de cozinha e modo simples (07, UX-02/05/07/08/09/11).

## Decisões que só o dono pode tomar
- Fornecedor de email para recuperação/verificação (ex.: Azure Communication Services, SendGrid, Brevo).
- Onde alojar a API e a web (Azure, como a especificação sugere, ou VPS).
- Aceitar a proposta de identidade visual do 07 (cores, tipos) e mudar as cores de membro de hex para nome (migração).

## Notas
- Os revisores deixaram contas de teste na base local (`qa-*@kubico.test`, `sec3a-*`/`sec3b-*@kubico.test`) e a casa "Casa QA" com as Tarefas partidas (QA-01); passos de limpeza no 03.
- O backend (01) foi revisto só lendo o código; os itens de QA e segurança marcados como provados correram contra a API local.
