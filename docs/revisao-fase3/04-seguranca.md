# Kubico — Revisão fase 3: segurança e privacidade (SEC)

Âmbito: branch `mvp-continuacao` @ 60bb5a1, leitura do backend (`backend/src`), da app (`app/lib`, `app/web`, `app/android`), Docker/CI e histórico git; provas contra a API local (Docker, Production) em http://localhost:5281 com contas `sec3a-*/sec3b-*@kubico.test` (script: `C:\Users\Badillon\AppData\Local\Temp\claude\C--Users-Badillon-Projetos-Kubico\d5b5d637-23af-4c02-837a-898893c9d700\scratchpad\sec_probe.py`). Nenhum ficheiro do repositório foi alterado.

## O que está bem (confirmado, não repetir)
- **BOLA entre casas**: B a ler a casa de A → 404 em `/households/{id}`, `/lists`, `/chores`, `/events`, `/rewards`, `/allowances`, `/activity`, `/absences`, `/permissions` (prova). Todas as consultas por id de entidade filtram por `HouseholdId` (revisão de todos os `Where(x => x.Id == …)`; os do `ActivityEndpoints.NotifyAsync` usam chaves gravadas pelo próprio servidor).
- Refresh token do perfil de criança deixa de servir depois de mudar o PIN (401, prova). Fotos servidas com `nosniff` e CSP `default-src 'none'`. Escopo Google mínimo (`calendar.app.created`). Página do OAuth com CSP própria, escape de HTML e confirmação explícita. `CloseOnAuthenticationExpiration` no hub; grupos SignalR por área e reacertados ao mudar papéis. Sem segredos reais no histórico git (só as chaves públicas do Firebase e a chave JWT de desenvolvimento, que a API recusa fora de Development). `.env` e `.claude/contas-de-teste.md` nunca foram commitados. Contentor da API corre como `$APP_UID` (não root); portas só em 127.0.0.1.

---

## Alto

### SEC-01 — O endereço do calendário subscrito (ICS) sobrevive a mudar o PIN, renovar o código da família e terminar sessões
- **Onde**: `Calendar/CalendarFeedEndpoints.cs:47-58` (criação), `Households/ChildProfileEndpoints.cs:178-180` (ChangePin), `:252-262` (RevokeFamilyCode), `Auth/SessionService.cs:133-140` (`SignOutEverywhereAsync` não toca em `CalendarFeedToken`), `Auth/AccountEndpoints.cs:268` (ligar 2FA), `Auth/AuthEndpoints.cs:187` (`logout-all` só revoga com `ResetCalendarFeeds=true`).
- **Cenário**: alguém vê o PIN da criança (ou um adulto mal-intencionado), entra como ela, faz `POST /households/{id}/calendar-feed` (crianças têm `View` no calendário) e guarda o URL. Os pais descobrem e fazem exatamente o que a app recomenda: mudam o PIN e renovam o código da família. O URL continua a devolver, sem autenticação e para sempre, o calendário da família: horários, locais, "Leva/Vai buscar" com nomes de menores. O mesmo vale para um adulto que liga a 2FA ou termina todas as sessões após um roubo do telemóvel.
- **Prova**: `child creates feed 200` → `A changes child PIN 204` → `A revokes family code 204` → `feed AFTER … 200` (deveria ser 404).
- **Correção**: em `SignOutEverywhereAsync` (ou nos chamadores: ChangePin, SignOutChild, RevokeFamilyCode, EnableTwoFactor/DisableTwoFactor) chamar `CalendarFeedEndpoints.RevokeFeedsAsync(db, m => m.UserId == userId)`; `logout-all` com `ResetCalendarFeeds` a `true` por omissão; para perfis de criança, considerar não permitir feed de todo (ou só criado por um adulto). Guardar o token com hash SHA-256 (ver SEC-17). Teste: o cenário acima.

### SEC-02 — PIN das crianças adivinha-se em dias, e qualquer pessoa com o código da família bloqueia o perfil
- **Onde**: `Auth/ChildSignInEndpoints.cs:58-91`, `Auth/AttemptLimits.cs:16-30` (5 tentativas → 15 min, sempre igual), `Households/ChildProfileEndpoints.cs:284-290` (PIN de 4 a 6 algarismos, sem lista de PINs fracos).
- **Cenário**: o código da família é partilhado com crianças (fica escrito, dito em voz alta). Com ele, `POST /auth/child/profiles` devolve os ids e nomes das crianças sem autenticação. Ataque: 5 PINs a cada 15 min = 480/dia por perfil, sem escalada, sem aviso aos pais → PIN de 4 algarismos encontrado em ~10 dias em média (21 no pior caso); com PINs comuns (1234, 0000, ano de nascimento) em minutos. O limite por IP (10/min) não interessa, o binding é o bloqueio, e IPv6/proxies contornam-no de qualquer forma. Ao contrário, basta 5 pedidos a cada 15 min para a criança nunca conseguir entrar (DoS).
- **Prova**: 5 PINs errados → `child login with right PIN after 5 wrong 429 "Demasiados PINs errados… 15 minutos"`. Nenhum aviso foi criado para os adultos (não há `NotifyAsync` no caminho do bloqueio).
- **Impacto**: sessão de criança = calendário da casa, listas, nomes; combinado com SEC-01, acesso permanente.
- **Correção**: bloqueio progressivo (15 min → 1 h → 24 h, e depois "só um adulto desbloqueia" com `ChangePin`), aviso aos que gerem os perfis ao primeiro bloqueio (`NotificationService`), recusar PINs triviais (sequências, repetidos, 19xx/20xx), contar também tentativas por código da família (todas as crianças da casa), e opcionalmente exigir 6 algarismos.

### SEC-03 — "Quem mantém o Kubico" decide-se pelo email, e os emails não são verificados
- **Onde**: `Support/FeedbackEndpoints.cs:21-31` e `:82-110` (`IsAdmin(email)` → lista as últimas 200 mensagens com nome e email de quem as enviou), `Auth/AuthEndpoints.cs:67-97` (registo sem verificação de email), `Households/HouseholdEndpoints.cs:103` (`MeDto.isSupportAdmin`).
- **Cenário**: `Support:AdminEmails` contém um endereço que (ainda, ou já) não tem conta — porque se configurou antes de criar a conta, ou porque essa pessoa apagou a conta. Qualquer um regista esse email (sem verificação) e passa a ler as mensagens de suporte de todos os utilizadores, com os seus emails (dados pessoais; mensagens de "reportar problema" contêm frequentemente detalhes da família). O mesmo problema de fundo permite registar o email de outra pessoa e ocupá-lo (ela nunca mais consegue criar conta: 409).
- **Correção**: verificação de email no registo (link/código; contas não verificadas não contam para `IsAdmin` nem recebem convites), ou identificar os administradores de suporte por `UserId` (configuração ou tabela), e pôr `.RequireActiveSession()` em `GET /feedback`.

### SEC-04 — Não há forma de mudar nem recuperar a palavra-passe
- **Onde**: único `PasswordHash =` em `Auth/AuthEndpoints.cs:91` (registo); não existe endpoint de mudança nem de recuperação (`grep ChangePassword|reset` vazio; `app/lib/src/ui/account_screen.dart` não tem a opção).
- **Cenário**: palavra-passe exposta (reutilizada noutro site, vista por alguém). A pessoa pode terminar sessões, mas o atacante volta a entrar com a mesma palavra-passe; a única "rotação" é apagar a conta (e com ela as casas onde é a única com conta). Esquecer a palavra-passe = perder a conta e, sendo o único administrador, a administração da casa.
- **Correção**: `PUT /api/account/password` (palavra-passe atual + nova, `CheckPassword`, `RequireActiveSession`, termina as outras sessões e revoga feeds) e recuperação por email (depende de SEC-03: email verificado), com token de uso único e curto, que não revela se o email existe.

---

## Médio

### SEC-05 — Qualquer pessoa bloqueia a entrada de qualquer conta só com o email
- **Onde**: `Auth/LoginThrottle.cs:34-111` (chave = email, bloqueia "mesmo com a certa"), usado também em `AuthEndpoints.CheckPassword` (apagar a conta, ligar/desligar 2FA).
- **Prova**: 5 palavras-passe erradas enviadas por um terceiro → login de A com a palavra-passe certa: `429 "… daqui a um minuto"`. Repetindo, sobe para 5 e depois 15 min; 20 pedidos/hora mantêm a pessoa permanentemente fora (as sessões abertas continuam, mas não entra noutro aparelho nem consegue apagar a conta/desligar 2FA).
- **Correção**: contar por (email, IP) para o bloqueio "duro" e por email só com limite alto e degraus curtos; ou deixar passar a palavra-passe certa vinda de um IP/dispositivo já conhecido (refresh token ativo), ou exigir um desafio (CAPTCHA/prova de trabalho) em vez de bloquear. Avisar o dono da conta quando há bloqueios.

### SEC-06 — Códigos de convite sem limite de pedidos, e convites de Convidado/Criança com usos ilimitados por omissão
- **Onde**: `Households/HouseholdEndpoints.cs:75-76` (`GET /invites/{code}` e `POST …/accept` sem `RequireRateLimiting`), `:229` (`MaxUses` nulo para tudo menos Adulto), `:56-57` (8 caracteres de 31).
- **Prova**: 40 códigos aleatórios seguidos com a mesma conta → só 404, nenhum 429.
- **Cenário**: o espaço (31^8 ≈ 8,5×10^11) aguenta um atacante isolado, mas com N convites ativos na plataforma a probabilidade por tentativa é N/8,5×10^11 e não há travão nenhum (uma conta, vários pedidos em paralelo). Mais grave e prático: um link de Convidado partilhado num grupo e reencaminhado deixa entrar quantas pessoas quiserem durante 30 dias, sem aprovação; um Convidado vê por omissão o calendário (horários e locais das crianças), as fotos de prova (SEC-08) e os pontos.
- **Correção**: política de rate limit por utilizador+IP nestes dois endpoints (ex.: 20/min, 200/dia), `MaxUses = 1` por omissão para todos os papéis (a app escolhe explicitamente "vários"), aviso ao administrador quando alguém entra, e opcionalmente "entrada pendente de aprovação" para convites com vários usos.

### SEC-07 — Fotos de prova sem quota nem limite: qualquer criança ou convidado enche a base de dados
- **Onde**: `Chores/ChoreHistoryEndpoints.cs:49-87` (só `Participate`; sem limite por pessoa/dia; os bytes vão para o PostgreSQL), `ChorePhoto.KeepUnusedFor` = 1 dia; nenhum `RequestSizeLimit`/`FormOptions` no projeto (o Kestrel aceita até ~30 MB antes do `photo.Length` ser verificado).
- **Prova**: 15 envios de 1 MB seguidos → todos 201. A 5 MB por pedido, um script faz dezenas de GB/dia por conta; as não usadas só saem passado 1 dia (e só no envio seguinte da casa ou na limpeza diária).
- **Também**: o `StripJpeg` copia tudo o que vem depois do SOS, incluindo bytes depois do EOI (prova: `<script>` mantido no ficheiro devolvido). Hoje é inofensivo (nosniff + CSP), mas é um ficheiro poliglota guardado e servido pela API.
- **Correção**: `[RequestSizeLimit(ChorePhoto.MaxBytes + 64 KB)]`/`.WithMetadata(new RequestSizeLimitAttribute(...))` no endpoint, quota por pessoa (ex.: 30 fotos não usadas/dia) e por casa (MB totais), e cortar no EOI (JPEG) / `IEND` já feito no PNG. Idealmente reencodar a imagem (resolve metadados restantes, APP2/APPn, e poliglotas).

### SEC-08 — Fotos de prova (imagens de menores) visíveis a todos os que veem as Tarefas, incluindo Convidados e outras crianças
- **Onde**: `Chores/ChoreHistoryEndpoints.cs:95-104` (`GetPhoto` pede só `ModuleAccess.View`), `:128-148` (`GetHistory` dá o `PhotoId` de todas as conclusões); `ModulePermissions.Default` dá `Participate` aos Convidados.
- **Cenário**: a criança fotografa o quarto arrumado; a ama/empregada (Convidado) ou o primo (Criança de outra família numa casa partilhada) vê todas as fotos dos últimos 90 dias. Minimização de dados (RGPD art. 5.º/1-c, e art. 8.º para menores).
- **Correção**: a foto só para quem a enviou e para quem gere as Tarefas (`level >= Manage || p.UploadedByUserId == me`), e o `PhotoId` só no histórico para esses.

### SEC-09 — Ligar o iCloud transforma o servidor num oráculo de Apple IDs, e a autenticação Basic segue redirecionamentos para qualquer sítio
- **Onde**: `Calendar/Sync/CalendarSyncEndpoints.cs:288-356` (`ConnectApple` sem rate limit; basta criar uma casa para ter `Manage`), `Calendar/Sync/AppleCalendarProvider.cs:449-480` (segue 301/302/307/308 para qualquer `Location`, incluindo `http://` e outro domínio, reenviando `Authorization: Basic`), `:414-425` (`href` do servidor resolvido sem confirmar o host).
- **Cenário**: qualquer conta regista-se, cria uma casa e testa milhares de pares Apple ID/palavra-passe de app a partir do IP do Kubico (400 vs 502 diz se estão certos). A Apple pode bloquear o IP do servidor e cortar o iCloud a todos. Em defesa em profundidade, um redirecionamento/`href` absoluto para outro host recebe as credenciais em claro.
- **Correção**: rate limit por utilizador (ex.: 5 tentativas/hora) e `RequireActiveSession`; só seguir redirecionamentos/hrefs `https` para `*.icloud.com` (ou o host de `ServerUrl`), senão erro.

### SEC-10 — Dados de um perfil de criança tirado da casa ficam: as fotos até 90 dias
- **Onde**: `Households/MembershipService.cs:59-63` apaga o `User` do perfil, mas não as `ChorePhotos` dele (só o `DeleteAccount` das contas adultas as apaga, `AccountEndpoints.cs:438-444`); as conclusões continuam a apontar para o `PhotoId` e aparecem no histórico.
- **Correção**: ao apagar um perfil de criança (e no `RemoveAsync` em geral, para o que é da pessoa nessa casa), apagar as fotos dela e pôr `PhotoId = null` como no `DeleteAccount`. Os perfis de criança também não têm exportação RGPD feita por um adulto (art. 15.º exercido pelos pais): acrescentar `GET /households/{id}/children/{userId}/export` para quem gere os perfis.

### SEC-11 — CSP da app web demasiado larga
- **Onde**: `app/web/index.html:29`: `script-src … https://www.gstatic.com https://cdn.jsdelivr.net` (domínios inteiros; o jsdelivr serve qualquer pacote npm/GitHub, conhecido gadget de bypass de CSP), `connect-src … https: wss:` (placeholder que ainda está lá), sem SRI nos scripts externos (ZXing do `mobile_scanner`, Firebase); `frame-ancestors` não funciona em `<meta>` (clickjacking da app web depende do alojamento). No browser, o `flutter_secure_storage` guarda o refresh token cifrado com uma chave que também fica no `localStorage` — qualquer XSS o lê.
- **Correção**: restringir a caminhos (`https://cdn.jsdelivr.net/npm/@zxing/library@<versão>/`, `https://www.gstatic.com/firebasejs/13.0.0/`) ou alojar esses ficheiros na própria origem; trocar `https: wss:` pela API real no build de produção (substituição no CI); `frame-ancestors 'none'` e a CSP como cabeçalho no alojamento.

### SEC-12 — Fora de Development e de Production (ex.: Staging) a API usa a chave-mestra pública e o cabeçalho Host
- **Onde**: `Common/MasterKeyEncryption.cs:43-56` (sem chave → a de desenvolvimento, que está no código, em qualquer ambiente que não seja Production), `Common/ProductionChecks.cs:35-55` (`PublicBaseUrl` só exigido em Production), `Common/PublicUrls.cs:9-10` (sem ele, `request.Host`). Em Production também não se recusa a `DataProtection:MasterKey` igual à de desenvolvimento.
- **Cenário**: um servidor de testes com `ASPNETCORE_ENVIRONMENT=Staging` e contas reais do Google/iCloud guarda os refresh tokens e as palavras-passe de app cifrados com uma chave conhecida; os `redirect_uri` e os URLs dos feeds vêm do Host (host header injection, se `AllowedHosts` ficar `*`, como em `appsettings.json`).
- **Correção**: exigir chave-mestra e `PublicBaseUrl` em todos os ambientes que não sejam Development, e recusar a chave de desenvolvimento fora de Development (como já se faz com a do JWT).

---

## Baixo

### SEC-13 — O registo revela quem tem conta
`Auth/AuthEndpoints.cs:77-82`: 409 "Este email já está registado." (prova: `register duplicate 409`), anulando o cuidado do login com o hash fictício e o tempo igual. Com verificação de email (SEC-03), responder sempre igual ("enviámos um email") e avisar o dono por email.

### SEC-14 — O tipo de dispositivo (e a duração da sessão) é escolhido pelo cliente; sessões sem limite absoluto
`Auth/AuthEndpoints.cs:279-299` e `SessionService.cs:162-165`: um browser que envie `X-Kubico-Device: android` recebe um refresh token de 60 dias em vez de 14 (prova: `refreshTokenExpiresAt` = +60 dias com User-Agent de Chrome). A rotação renova a família indefinidamente (nunca há reautenticação). Correção: decidir "web" também pelo `Origin` (pedidos CORS = web) e pôr um limite absoluto por família (ex.: 180 dias, 30 na web).

### SEC-15 — Permissões pedidas às contas externas maiores do que o necessário
`MicrosoftCalendarProvider.cs:20`: `Calendars.ReadWrite` dá acesso a todos os calendários do Outlook (o Graph não tem equivalente ao `app.created`; documentar e mostrar ao utilizador). A palavra-passe de app da Apple dá acesso a email, contactos e calendários: dizê-lo no ecrã de ligação e sugerir revogá-la ao desligar.

### SEC-16 — Um token push pode ser "roubado" por outra conta
`Notifications/NotificationEndpoints.cs:131-160`: registar um FID que já existe muda-o para quem pede, sem prova de posse. Quem souber o FID de outra pessoa deixa-a sem avisos e passa a enviar-lhe os seus. Correção: só reatribuir se a sessão anterior do token terminou, ou guardar por (token, utilizador).

### SEC-17 — Tokens do calendário subscrito guardados em claro
`Membership.CalendarFeedToken` é comparado diretamente (`CalendarFeedEndpoints.cs:91-95`), ao contrário dos refresh tokens (SHA-256). Uma cópia da base dá acesso imediato a todos os calendários subscritos. Guardar o hash (o URL só se mostra ao criar; "mostrar outra vez" passa a "gerar novo").

### SEC-18 — Cadeia de fornecimento e CI
`.github/workflows/ci.yml`: ações por tag (`@v4`, `@v2`) e não por SHA, sem bloco `permissions:` (token com as permissões por omissão), sem `dotnet list package --vulnerable`/`flutter pub outdated`/Dependabot; imagens `postgres:17`, `sdk:10.0`, `aspnet:10.0` sem digest. As chaves do Firebase no repositório são públicas por desenho, mas confirmar na consola do Google Cloud que estão restritas (pacote Android + SHA-1, referrer da web, só as APIs do Firebase).

### SEC-19 — 2FA: bloqueio fixo de 15 minutos sem escalada
`AttemptLimits.cs:37-51`: 480 códigos/dia; com a janela de ±1 passo (3 códigos válidos), ~0,14 %/dia, ~40 % num ano para quem já tem a palavra-passe. Pouco provável, mas o mesmo bloqueio progressivo de SEC-02 resolve, e avisar o dono quando há códigos errados (sinal de palavra-passe comprometida — ver SEC-04).

### SEC-20 — Pequenas fugas de informação
- `Shopping/ShoppingEndpoints.cs:220-226`: com um id de artigo de outra casa, `AddItem` responde 409 "Este artigo já existe noutra lista" (oráculo de existência; GUIDs aleatórios, impacto quase nulo — responder como se fosse novo id em conflito genérico).
- `POST /auth/child/profiles` devolve nomes e cores das crianças a quem tiver o código da família, sem sessão (aceitável, mas mostrar no ecrã do código da família que ele revela os nomes).
- O token do SignalR vai na query string (`access_token`): garantir que o proxy de produção não regista query strings em `/hubs`.

---

## Top 5 (o que mais vale a pena fazer)
1. **SEC-01** — revogar os feeds ICS em todas as terminações de sessão (PIN, código da família, 2FA, logout-all) e guardar o hash (SEC-17). Pequeno e fecha um acesso permanente aos horários das crianças.
2. **SEC-02** — bloqueio progressivo do PIN, aviso aos pais, PINs triviais recusados.
3. **SEC-03 + SEC-04** — verificação de email, mudar e recuperar a palavra-passe, e administradores de suporte por id.
4. **SEC-05 + SEC-06** — bloqueio de login por (email, IP) e rate limit nos convites, com `MaxUses = 1` por omissão.
5. **SEC-07 + SEC-08** — limite de tamanho/quota nas fotos e fotos só para quem as enviou e quem gere as Tarefas.
