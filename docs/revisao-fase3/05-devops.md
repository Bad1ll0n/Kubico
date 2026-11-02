# Kubico — Revisão fase 3: DevOps / SRE (OPS)

Revisor: DevOps/SRE sénior. Base: branch `mvp-continuacao`, commit 60bb5a1. Leitura de `backend/Dockerfile`, `backend/.dockerignore`, `docker-compose.yml`, `.env.example`, `.github/workflows/ci.yml`, `Program.cs`, `appsettings.json`, workers (`MaintenanceWorker`, `NotificationWorker`, `CalendarSyncWorker`), `PushSenders.cs`, `MasterKeyEncryption.cs`, `ProductionChecks.cs`, `DependencyInjection.cs`, README.

Inspeção da stack em Docker (sem parar nem recriar nada):
- `kubico-api-1` corre como `uid=1654(app)` (bom), **RestartPolicy `no`**, **sem healthcheck**, sem limites de memória, `ReadonlyRootfs=false`, sem `cap_drop`/`no-new-privileges`. Imagem `kubico-api` com **408 MB** (Ubuntu 24.04 com shell, sem `curl`/`wget`). `tzdata` (Europe/Lisbon) e ICU presentes.
- `kubico-db-1` (`postgres:17`, tag flutuante) healthy, RestartPolicy `no`, um volume local `kubico_kubico-db`; `archive_mode=off`, `shared_buffers=128MB`, `max_connections=100`. **A API liga-se com o papel `kubico`, que é superutilizador** (`rolsuper = t`).
- `GET /health` → 200 `Healthy` (só verifica a base). Logs em texto multi-linha do formatador por omissão; aviso EF `Skip/Take sem OrderBy` logo no arranque; aviso `Overriding HTTP_PORTS '8080'… Binding to … URLS`.

---

## Crítico

### OPS-01 — Não há cópias de segurança nem plano de recuperação (base + chave-mestra)
**Onde:** `docker-compose.yml:16-17` (só um volume local), PostgreSQL com `archive_mode=off`; nada no repositório faz `pg_dump`/WAL; README, secção "Produção: o que configurar" (`README.md:82-91`) nem menciona `DataProtection:MasterKey`.
**Cenário de falha:** disco do servidor morre, `docker compose down -v` por engano, ou corrupção → perdem-se todas as casas, tarefas, pontos, mesadas pagas, fotos de prova (guardadas na base) e o histórico. Sem PITR, um `DELETE` errado de um deploy não tem volta. Pior: se houver cópia da base mas não da `DATA_PROTECTION_MASTER_KEY` (o `.env` está fora do Git, por desenho), a 2FA de todos deixa de funcionar (só entram com códigos de recuperação) e todas as ligações Google/Outlook/iCloud ficam inutilizáveis. O inverso também: chave e base no mesmo backup anulam o propósito da chave-mestra.
**Correção proposta:**
1. Produção gerida: PostgreSQL gerido com PITR (Azure Database for PostgreSQL Flexible Server: retenção 14–35 dias, backup geo-redundante numa região UE). Em VPS: `pgBackRest` ou `WAL-G` com `archive_mode=on` para object storage noutro fornecedor/região, mais `pg_dump -Fc` diário cifrado (retenção 7 diários / 4 semanais / 6 mensais).
2. Chave-mestra, `Jwt:SigningKey` e conta de serviço FCM num cofre (Azure Key Vault com soft-delete + purge protection), **com uma cópia em escrow offline** (ex.: gestor de palavras-passe da organização, 2 pessoas), nunca no mesmo destino dos backups da base. Registar a impressão digital (`Fingerprint`, `MasterKeyEncryption.cs:36`) no runbook para validar a chave certa.
3. Runbook de DR escrito e **ensaiado trimestralmente**: restaurar o último backup numa base nova, arrancar a API com a chave do cofre, validar (`/health/ready`, login com 2FA de teste, uma ligação de calendário de teste sincroniza). Objetivos sugeridos: RPO ≤ 5 min (WAL), RTO ≤ 2 h.
4. Alerta se o último backup bem-sucedido tiver mais de 26 h (ver OPS-04).

---

## Alto

### OPS-02 — Contentores sem política de reinício nem healthcheck da API; a API morre se a base não estiver pronta
**Onde:** `docker-compose.yml:24-40` (sem `restart:` nem `healthcheck:` no `api`; `db` também sem `restart:`); `backend/Dockerfile:13-19` (sem `HEALTHCHECK`); `Program.cs:194` (`MigrateAsync` no arranque).
**Cenário de falha:** reinício do host ou do Docker → nada volta a arrancar. Um `OutOfMemory`/crash da API → fica parada até alguém reparar. O `depends_on: service_healthy` só vale no primeiro `up`: se a base reiniciar e a API reiniciar antes de o Postgres aceitar ligações, `MigrateAsync` lança, o processo termina e, com `restart: no`, fica morto. Sem healthcheck, um proxy/orquestrador não sabe tirar a API de serviço.
**Correção proposta:** `restart: unless-stopped` nos dois serviços; healthcheck na API. Como a imagem não tem `curl` (e não deve passar a ter, ver OPS-11), acrescentar um modo de sonda ao próprio binário (`dotnet Kubico.Api.dll --probe http://localhost:8080/health/live`, que faz um GET e devolve 0/1) e usá-lo em `HEALTHCHECK`/`healthcheck:` (interval 15 s, start_period 60 s para as migrações). `stop_grace_period: 30s` (ver OPS-17). Opcional: retry com backoff curto à volta do `MigrateAsync` (ou mover as migrações para fora do arranque, OPS-10).

### OPS-03 — Observabilidade quase inexistente (a especificação pede Application Insights)
**Onde:** `appsettings.json:2-8` (só níveis); `Program.cs` sem OpenTelemetry, sem Application Insights, sem formatador JSON, sem `UseHttpLogging`/correlação; nenhum `Meter`/`ActivitySource` no código.
**Cenário de falha:** em produção, um pico de 500 ou de latência num endpoint só se descobre por queixa de uma família. Logs em texto multi-linha (formatador por omissão) partem-se por linhas em qualquer agregador; não há `TraceId` a ligar o pedido da app à exceção; não se sabe quantos avisos saem nem quanto demora uma sincronização.
**Correção proposta:**
- `Azure.Monitor.OpenTelemetry.AspNetCore` (`builder.Services.AddOpenTelemetry().UseAzureMonitor()`), ativado só quando existe `APPLICATIONINSIGHTS_CONNECTION_STRING` (testes e local sem nada). Dá pedidos, dependências (Npgsql, HttpClient para FCM/Google/Graph/CalDAV), exceções e logs correlacionados por `TraceId`. Acrescentar `Npgsql.OpenTelemetry` (`AddNpgsql()`) para as consultas.
- Em contentor sem App Insights: `builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; })` em Production.
- Garantir que nada sensível vai para a telemetria: a query `access_token` do SignalR (`Program.cs:131`) aparece em URLs de pedidos `/hubs` — filtrar/redigir `url.query` no processador do OTel; não registar corpos de `/api/auth` nem `/api/account/2fa`.
- Amostragem (ex.: 20–30 % dos pedidos com sucesso, 100 % de falhas) para controlar custo.
- Metadados de serviço: `service.name=kubico-api`, `service.version` = SHA do commit (passado como build-arg, ver OPS-14).

### OPS-04 — Falhas de sincronização de calendários e de avisos não geram sinal nem alerta
**Onde:** `CalendarSyncService.cs:148-158` (perda de acesso só `LogInformation`; falha `LogWarning`); `CalendarSyncSupport.cs:208-216,242-244` (erros do worker só em log); `PushSenders.cs:104` (`LogWarning`); `NotificationDispatcher.cs:184-186`; `/health` (`Program.cs:159,217`) não sabe nada dos workers.
**Cenário de falha:** a conta de serviço FCM é revogada ou o token endpoint da Google falha → `AccessTokenAsync` lança em cada minuto, nenhum aviso chega a nenhum telemóvel e o `/health` continua `Healthy`. O segredo OAuth do Google expira (os de Entra ID expiram por desenho a 6/12/24 meses) → todas as ligações passam a `NeedsReconnect` com uma linha de Information cada. Um `BackgroundService` que lance fora dos `try` (ex.: `trigger.Requests.ReadAsync` ou `Task.Delay` com exceção inesperada) termina o host em .NET 8+ (`BackgroundServiceExceptionBehavior.StopHost`) — com `restart: no` (OPS-02) fica tudo parado.
**Correção proposta:**
- Métricas (`System.Diagnostics.Metrics`, `Meter("Kubico")`, exportadas pelo OTel de OPS-03): `kubico.push.sent`, `kubico.push.failed{reason}`, `kubico.push.invalid_token`, `kubico.notifications.pending_age_seconds` (idade do aviso por enviar mais antigo), `kubico.calendar.sync.runs{provider,result=ok|failed|needs_reconnect}`, `kubico.calendar.sync.duration`, `kubico.worker.last_success{worker}`.
- Health check `workers` (tag `ready`, não `live`) que fica `Degraded` quando um worker não tem uma volta bem-sucedida há mais de 3× o intervalo (3 min para avisos, 15 min para sync, 26 h para limpezas).
- Passar para `LogWarning`/`LogError` estruturado a passagem a `NeedsReconnect` com `{Provider}`, e logar o motivo do `AccessTokenAsync` do FCM.
- Regras de alerta (Azure Monitor, ação para e-mail/Teams/PagerDuty):
  | Sinal | Limiar |
  |---|---|
  | `/health/ready` a falhar | 2 verificações seguidas (availability test de 1 em 1 min, 2 regiões) |
  | 5xx da API | > 2 % dos pedidos em 5 min, ou > 20 em 5 min |
  | Latência p95 | > 1,5 s durante 10 min |
  | Aviso por enviar mais antigo | > 10 min (fora de horas de silêncio, que não entram na consulta) |
  | `push.failed / (sent+failed)` | > 10 % em 15 min |
  | `calendar.sync.runs{result=failed}` | > 25 % das ligações numa volta, 3 voltas seguidas |
  | `needs_reconnect` por provider | > 5 em 1 h (provável segredo OAuth expirado) |
  | Worker sem sucesso | ver limiares acima |
  | Backup | último backup > 26 h; espaço da base > 80 % |
  | 429 nos endpoints de login | pico > 10× a média (ataque ou OPS-07) |
  | Certificado | expira em < 14 dias (se não for gerido) |
- Lembrete no calendário da equipa para a validade dos segredos OAuth (Entra ID) — ou alerta no Key Vault de "near expiry".

### OPS-05 — Envio de avisos: uma falha de rede aborta o lote todo, há reenvios em duplicado e falhas permanentes repetem-se para sempre
**Onde:** `NotificationDispatcher.cs:111-135`; `PushSenders.cs:91-105,125`.
**Cenários de falha:**
1. `http.SendAsync` lança (`HttpRequestException`, timeout de 100 s do `HttpClient`) para **um** telemóvel → o `Parallel.ForEachAsync` falha, o lote de até 500 pessoas não chega ao `SaveChangesAsync`: nada fica marcado `PushedAt`, nem os avisos "só na app", nem os tokens inválidos removidos, e os envios que já saíram voltam a sair no minuto seguinte. O mesmo se o token endpoint da Google falhar (`EnsureSuccessStatusCode`, linha 125).
2. Um job (pessoa) com dois telemóveis em que um falha (`PushResult.Failed`) é todo repetido (linha 123: o `failed` é por job) → o telemóvel que recebeu volta a receber a cada minuto enquanto o outro falhar.
3. Respostas permanentes do FCM (400 `INVALID_ARGUMENT` com FID mal formado, 403 `SENDER_ID_MISMATCH`) devolvem `Failed` e são repetidas a cada minuto, sem limite, até o aviso ser apagado aos 90 dias — ruído nos logs e duplicados nos outros telemóveis da pessoa.
**Correção proposta:** apanhar exceções dentro do `SendAsync` (devolver `Failed` em vez de lançar); resultado por (aviso, dispositivo) em vez de por job, ou pelo menos marcar como enviado se pelo menos um dispositivo recebeu e só repetir os que falharam; tratar 400 `INVALID_ARGUMENT`/403 `SENDER_ID_MISMATCH` como `InvalidToken`; respeitar `Retry-After` em 429/503; contador de tentativas (`PushAttempts`) com desistência após N (ex.: 10, cerca de 1 h com backoff) e métrica `push.failed{reason=gave_up}`.

### OPS-06 — O CI nunca corre contra PostgreSQL nem aplica as migrações
**Onde:** `.github/workflows/ci.yml:9-22`; os testes só usam SQLite (`tests/Kubico.Api.Tests`, nenhuma referência a Postgres/Testcontainers); `Program.cs:262-268` (em SQLite faz `EnsureCreated`, nunca `Migrate`).
**Cenário de falha:** o CLAUDE.md exige que a concorrência "funcione em SQLite e em PostgreSQL", mas o SQLite serializa escritores — os `ConcurrencyTests` passam lá sem provar o comportamento do Postgres (níveis de isolamento, índices únicos parciais, `ExecuteUpdateAsync` condicional, tokens de concorrência). E uma migração partida (ou uma alteração ao modelo sem migração) só se descobre no deploy, com a API a falhar no arranque (OPS-02).
**Correção proposta:** job `backend-postgres` com `services: postgres:17` (ou Testcontainers) e a fábrica de testes a ler `KUBICO_TEST_PROVIDER=Postgres` (pelo menos `ConcurrencyTests`, `CalendarEquivalenceTests` e um smoke por área); passos `dotnet ef migrations has-pending-model-changes` (falha se faltar migração) e `dotnet ef migrations bundle`/`script --idempotent` publicado como artefacto, para o deploy (OPS-10).

### OPS-07 — Atrás de um proxy em contentor, todos os clientes parecem o mesmo IP (limites de pedidos globais)
**Onde:** `Program.cs:180-187` (só `KnownProxies` com IPs; `KnownNetworks` por omissão = loopback); `appsettings.json:35-37`; `RateLimits` por IP.
**Cenário de falha:** em produção o proxy (Caddy/Traefik num contentor, o Envoy do Azure Container Apps, o Front Door) chega de um IP da rede Docker/VNet, dinâmico e não-loopback. O `X-Forwarded-For` é ignorado, o `RemoteIpAddress` é o do proxy e os limites de `RateLimits` passam a ser **para o país inteiro**: 10 logins por minuto, 5 registos por minuto, 30 renovações por minuto (a renovação acontece de 15 em 15 min por cada app aberta — com ~500 utilizadores ativos já dá 429 em massa e sessões a cair). Fixar `KnownProxies` não serve quando o IP muda a cada recriação.
**Correção proposta:** aceitar também `ForwardedHeaders:KnownNetworks` (CIDR, `options.KnownNetworks.Add(IPNetwork.Parse(...))`), com a sub-rede da rede Docker fixada no compose (`networks: … ipam: subnet 172.30.0.0/24`) ou a da VNet do ACA; teste de integração que confirme que o limitador usa o IP do cliente quando o proxy está na rede conhecida. Num CDN/Front Door: `ForwardLimit` adequado e confiar só nos intervalos publicados.

---

## Médio

### OPS-08 — A API usa um superutilizador do PostgreSQL
**Onde:** `docker-compose.yml:10-12,31` (o `POSTGRES_USER` da imagem oficial é superutilizador; confirmado `rolsuper = t`).
**Cenário de falha:** qualquer falha que permita SQL arbitrário (ou uma dependência comprometida) tem `COPY … TO PROGRAM`, leitura de ficheiros do servidor e `DROP DATABASE`. Também impede separar quem migra de quem serve.
**Correção proposta:** script de init (`/docker-entrypoint-initdb.d/`) que cria `kubico_owner` (dono do esquema, usado só pelas migrações) e `kubico_app` (`CONNECT`, `USAGE`, `SELECT/INSERT/UPDATE/DELETE` e `ALTER DEFAULT PRIVILEGES`), sem `SUPERUSER`/`CREATEDB`. Em PostgreSQL gerido é o mesmo modelo.

### OPS-09 — O compose de "produção" não passa a configuração dos avisos, calendários e proxy
**Onde:** `docker-compose.yml:29-38`; `.env.example`.
**Cenário de falha:** quem segue o compose para um servidor fica sem avisos no telemóvel (`LogPushSender`, `Program.cs:62-65`) e com a sincronização "indisponível", sem nenhum erro — só se nota quando alguém se queixa. `AllowedHosts` fica `*` (`appsettings.json:9`), contra o que o CLAUDE.md pede num servidor, e `ForwardedHeaders` não é configurável pelo `.env`.
**Correção proposta:** acrescentar ao compose (e ao `.env.example`) `CalendarSync__Google__*`, `CalendarSync__Microsoft__*`, `AllowedHosts`, `ForwardedHeaders__KnownNetworks__0`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, e a conta de serviço do FCM como **Docker secret** montado em ficheiro (`Push__Fcm__ServiceAccountFile=/run/secrets/fcm.json`), não em variável. Um log de arranque em Production a dizer o que está ativo ("Avisos FCM: sim/não; Google: sim/não; …") e um `Warning` se faltar algo.

### OPS-10 — Migrações aplicadas no arranque da API
**Onde:** `docker-compose.yml:32`; `Program.cs:194,249-270`; README `:87`.
**Cenário de falha:** num deploy com nova revisão a arrancar ao lado da antiga (ACA, rolling update), a nova migra o esquema enquanto a antiga ainda serve — uma coluna renomeada/removida parte a antiga; uma migração longa (índice numa tabela grande, fotos) atrasa o arranque além do `start_period` e o orquestrador mata o contentor a meio. A API precisa de permissões de DDL (OPS-08). Não há passo de rollback.
**Correção proposta:** em produção `Database__MigrateOnStartup=false`; o pipeline corre o `efbundle` (OPS-06) como passo próprio (job do ACA ou `docker compose run --rm migrate`) com o papel dono, antes de trocar a revisão; regra de migrações expand/contract (acrescentar → deploy → remover numa versão seguinte); backup/ponto de restauro imediatamente antes de migrar. Manter o `MigrateOnStartup` só para o compose local.

### OPS-11 — Imagem grande, tags flutuantes e endurecimento do contentor por fazer
**Onde:** `backend/Dockerfile:1,13,16-18`; `docker-compose.yml:8`; `backend/.dockerignore`.
**Problemas:** 408 MB com shell e gestor de pacotes (superfície de ataque); `sdk:10.0`, `aspnet:10.0` e `postgres:17` flutuantes → builds não reprodutíveis e uma minor do Postgres pode chegar sem aviso; `ASPNETCORE_URLS` duplica o `ASPNETCORE_HTTP_PORTS` da imagem (aviso no arranque); `appsettings.Development.json` vai para a imagem (confirmado em `/app`); sem `read_only`, `cap_drop: [ALL]`, `security_opt: no-new-privileges`, `mem_limit`/`cpus`, `pids_limit`.
**Correção proposta:** base `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (sem shell, ~120 MB; a variante **-extra** é obrigatória porque a app precisa de ICU para pt/en e de `tzdata` para Europe/Lisbon — a chiseled simples rebentaria em `TimeZoneInfo.FindSystemTimeZoneById`), já com utilizador `app` não-root; fixar por digest (`@sha256:…`) e deixar o Dependabot atualizar (OPS-14); retirar `ENV ASPNETCORE_URLS` (a imagem já expõe 8080); excluir `**/appsettings.Development.json` no publish (`<Content Update=… CopyToPublishDirectory="Never">`) ou no `.dockerignore`; no compose: `read_only: true` + `tmpfs: /tmp`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]`, `mem_limit: 512m`; `postgres:17.x` fixo. Acrescentar `DOTNET_gcServer=0`/`DOTNET_GCHeapHardLimit` só se a memória apertar. Scan da imagem (Trivy) no CI.

### OPS-12 — A chave-mestra do Data Protection não se pode rodar
**Onde:** `MasterKeyEncryption.cs:101-111,139-147` (um só `DataProtectionMasterKey`; a decifra recusa outra impressão digital).
**Cenário de falha:** a chave fuga (um `.env` copiado, um portátil perdido) e é preciso trocá-la: com a chave nova, todas as chaves do Data Protection guardadas deixam de decifrar → 2FA e calendários ligados partem-se para todos. Não há procedimento nem código para re-cifrar.
**Correção proposta:** `DataProtection:PreviousMasterKeys` (lista) usada só na decifra, escolhida pela impressão digital; ao arrancar com chave nova, re-cifrar as linhas de `DataProtectionKeys` (ou forçar uma chave nova do Data Protection e re-proteger os segredos guardados em lote). Documentar o procedimento no runbook de OPS-01 e testá-lo.

### OPS-13 — Um só endpoint de saúde, que mistura "vivo" com "pronto" e não é suficiente para orquestradores
**Onde:** `Program.cs:159,217`.
**Cenário de falha:** usado como liveness num ACA/Kubernetes, uma falha do Postgres (manutenção do serviço gerido) faz reiniciar todas as réplicas em ciclo, o que piora a recuperação e fecha todas as ligações SignalR. Usado como readiness, não sabe dos workers (OPS-04).
**Correção proposta:** `/health/live` (sem verificações, só "o processo responde") e `/health/ready` (base + workers, `Degraded` não tira de serviço), com `Predicate` por tag; `ResponseWriter` JSON só com estados (sem mensagens de exceção). Excluir os dois da telemetria de pedidos para não encherem o App Insights.

### OPS-14 — CI: sem imagem, sem deploy, sem higiene de fornecimento
**Onde:** `.github/workflows/ci.yml`; não há `.github/dependabot.yml`.
**Problemas:** não constrói a imagem Docker (um Dockerfile partido só se vê no servidor); `flutter-action` com `channel: stable` sem versão — o CLAUDE.md diz que os dados de datas pt_PT/en_GB têm de ser copiados a cada versão do Flutter, logo uma nova stable pode partir a web em silêncio ou partir o CI num dia qualquer; sem `permissions: contents: read`; ações por tag (`@v4`, `@v2`) e não por SHA; sem `concurrency` (cancelar corridas antigas do mesmo PR); sem cache do NuGet; sem `dotnet list package --vulnerable --include-transitive` nem `flutter pub outdated`; o push só dispara em `main` (o trabalho vive em `mvp-continuacao` e só tem CI se houver PR).
**Correção proposta:** fixar `flutter-version` (a mesma do `pubspec`/README) e atualizá-la por PR; Dependabot para `nuget`, `pub`, `github-actions` e `docker`; job `image` que faz `docker build` (com `build-arg` do SHA), Trivy (falha em HIGH/CRITICAL corrigíveis) e, em `main`, push para GHCR/ACR com tag do SHA; job de deploy com aprovação manual (environment `production`) que corre as migrações (OPS-10), troca a revisão e faz um smoke a `/health/ready` e a um login de teste; publicar o `build/web` como artefacto.

### OPS-15 — A app web não tem caminho de alojamento definido e a CSP de produção é manual
**Onde:** `app/web/index.html` (`connect-src 'self' https: wss: http://localhost:* ws://localhost:* …`, com um comentário a pedir para trocar à mão); `app/lib/src/config.dart:6-7` (`API_URL`/`WEB_URL` por `--dart-define`).
**Cenário de falha:** publica-se a web com a CSP de desenvolvimento (aceita qualquer `https:`/`wss:` e localhost — anula a proteção contra exfiltração) ou esquece-se o `--dart-define=API_URL` e a app aponta para `http://localhost:5281`. As regras de cache descritas no CLAUDE.md (no-cache com ETag em `index.html`, `flutter_bootstrap.js`, `main.dart.js`; longa no resto) não estão em nenhum ficheiro.
**Correção proposta:** passo de build web no pipeline que recebe `API_URL`/`WEB_URL` e gera a CSP final (substituição de um marcador no `index.html`, com o domínio da API em `connect-src` e `wss://api…`), e um ficheiro de configuração do alojamento versionado: `staticwebapp.config.json` (Azure Static Web Apps: cabeçalhos, `navigationFallback`, cache) ou `Caddyfile`/`nginx.conf` se for no mesmo servidor. Teste no CI que falha se o `index.html` publicado contiver `localhost`.

### OPS-16 — Ligações à base sem resiliência nem dimensionamento
**Onde:** `DependencyInjection.cs:301-311` (`UseNpgsql` sem `EnableRetryOnFailure`, sem `Maximum Pool Size`/`Timeout`/`Keepalive`); Postgres com `max_connections=100` por omissão.
**Cenário de falha:** failover/manutenção do Postgres gerido (acontece mensalmente) → alguns segundos de `NpgsqlException` transitórios que viram 500 (o `ApiExceptionHandler` só trata concorrência/pedidos mal formados). O pool do Npgsql por omissão (100) já iguala o `max_connections` do servidor; com o compose de migração, uma ferramenta de administração e o pool do Data Protection, esgota-se.
**Correção proposta:** `EnableRetryOnFailure(3, 2 s)` — atenção ao único `BeginTransaction` explícito (tem de passar a `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`); connection string com `Maximum Pool Size=40;Timeout=15;Command Timeout=30;Keepalive=30`; `NpgsqlException.IsTransient` → 503 com `Retry-After` no `ApiExceptionHandler`. No servidor: `shared_buffers` ~25 % da RAM, `log_min_duration_statement=500ms`, `pg_stat_statements` ligado.

---

## Baixo

### OPS-17 — Encerramento abrupto
**Onde:** compose sem `stop_grace_period` (Docker mata ao fim de 10 s); `HostOptions.ShutdownTimeout` por omissão 30 s.
**Efeito:** num deploy, um `SaveChanges` do dispatcher ou da sync a meio é morto (fica para a próxima volta, mas pode repetir avisos — ver OPS-05) e as ligações SignalR caem sem fecho limpo. **Correção:** `stop_grace_period: 30s` e `ShutdownTimeout = 25 s`.

### OPS-18 — Origem do aviso EF "Skip/Take sem OrderBy"
**Onde:** `NotificationDispatcher.cs:43-49` (`.Distinct().Where(...).Take(BatchSize)` sem ordenação). Inofensivo (o `seen` evita repetições), mas é o aviso que aparece em cada arranque em Production e esconde avisos reais. **Correção:** `.OrderBy(id => id)` depois do `Where` (ou ordenar pelo aviso mais antigo, que é mais justo).

### OPS-19 — README de produção desatualizado
**Onde:** `README.md:82-91`: diz que `PublicBaseUrl` é "opcional" (o `ProductionChecks.cs:194-198` recusa arrancar sem ele), não menciona `DataProtection__MasterKey`, `Https__Enforce`, `ForwardedHeaders`, `AllowedHosts`, backups. **Correção:** uma secção "Deploy" única (ou `docs/ops.md`) com a tabela de configuração, o runbook de DR e a de alertas — o CLAUDE.md também remete para lá.

### OPS-20 — Porta do PostgreSQL publicada no compose
**Onde:** `docker-compose.yml:13-15`. Está em 127.0.0.1 e comentada, mas é o mesmo ficheiro que se diz ser "como num servidor". **Correção:** passar para um `docker-compose.override.yml` (só local), deixando o ficheiro base sem portas da base.

---

## Caminho para produção (recomendação)

Como a especificação pede Application Insights e os utilizadores são em Portugal (RGPD, dados de crianças), a opção mais simples de operar é Azure numa região UE (Spain Central ou West Europe):

| Peça | Serviço | Notas |
|---|---|---|
| API | **Azure Container Apps**, 1 réplica (min=max=1 por causa de P18), 0,5 vCPU/1 GiB | HTTPS e certificado gerido no domínio `api.kubico.pt`; WebSockets suportados; sticky sessions não são precisas com 1 réplica. Ingress chega por Envoy: precisa de OPS-07 (`KnownNetworks` da sub-rede da VNet). Liveness `/health/live`, readiness `/health/ready`. |
| Base | **Azure Database for PostgreSQL Flexible Server** (Burstable B1ms/B2s para começar), PostgreSQL 17 | PITR 14–35 dias, backup geo-redundante, acesso só por VNet/private endpoint. Papéis separados (OPS-08). |
| Segredos | **Key Vault** + identidade gerida do ACA | `Jwt:SigningKey`, `DataProtection:MasterKey` (com escrow offline, OPS-01), conta de serviço FCM, segredos OAuth com data de expiração. |
| App web | **Azure Static Web Apps** (ou Storage + Front Door) | `app.kubico.pt`, com `staticwebapp.config.json` (OPS-15). |
| Observabilidade | **Application Insights** (workspace Log Analytics) + alertas | OPS-03/04; disponibilidade de `/health/ready` de 2 regiões. |
| Imagens | GHCR ou ACR, tag = SHA | Deploy por revisão no ACA; rollback = reativar a revisão anterior (desde que as migrações sejam expand/contract). |

Alternativa de baixo custo (VPS na UE, ex.: Hetzner): o mesmo compose com Caddy à frente (HTTPS automático, rede Docker com sub-rede fixa para OPS-07), `restart: unless-stopped`, WAL-G/pgBackRest para object storage noutro fornecedor, `node_exporter`/Grafana Cloud ou App Insights por connection string, e as atualizações do SO automáticas. Mais barato, mas a disponibilidade e os backups passam a ser responsabilidade da equipa.

Fluxo de deploy: PR verde (incluindo Postgres e imagem) → merge em `main` → imagem `:sha` publicada → aprovação → backup/ponto de restauro → `efbundle` com o papel dono → nova revisão → smoke `/health/ready` + login de teste → 100 % do tráfego (ou rollback automático se o smoke falhar).

## P18 — novidade a registar

Nada muda na decisão, mas há um pormenor que o plano atual ("os workers numa só instância via `CalendarSync:Enabled`…") não cobre: o `CalendarSyncTrigger` é um `Channel` em memória (`CalendarSyncSupport.cs:118-131`) alimentado pelo `HouseholdNotifier` da instância que recebeu o pedido, e consumido pelo `CalendarSyncWorker` dessa mesma instância. Se as instâncias "web" tiverem `CalendarSync:Enabled=false`, os pedidos que chegam a elas enchem o canal e **ninguém os lê**: a sincronização "logo a seguir à alteração" degrada-se em silêncio para a volta de 5 minutos (e o canal cresce, se não for limitado). Quando se escalar: o pedido de sync tem de ir por um meio partilhado (uma tabela `CalendarSyncRequests` com `INSERT … ON CONFLICT DO NOTHING` lida pelo worker, ou `LISTEN/NOTIFY` do Postgres), tal como o `HubConnections.Abort` precisa de passar pelo backplane. Os contadores do `LoginThrottle` podem ir para a base com o mesmo padrão do `AttemptLimits` (`ExecuteUpdateAsync` condicional), evitando Redis só para isso; o limitador por IP do ASP.NET é que precisa de Redis (ou de passar para o WAF/Front Door). Ordem sugerida: (1) topologia 1 worker + N web com o trigger partilhado; (2) Azure SignalR Service (modo default) — resolve backplane e sticky sessions; (3) contadores partilhados; (4) `pg_try_advisory_lock` nos workers como rede de segurança.

---

## Top 5

1. **OPS-01** — Backups com PITR, chave-mestra num cofre com escrow separado, runbook de DR ensaiado. Hoje um disco perdido é a perda total.
2. **OPS-02 + OPS-13** — `restart: unless-stopped`, healthcheck pelo próprio binário, `/health/live` e `/health/ready`.
3. **OPS-03 + OPS-04** — Application Insights (OpenTelemetry), logs JSON, métricas de avisos e de sync, alertas da tabela acima.
4. **OPS-07** — `KnownNetworks` para o proxy: sem isto, os limites de pedidos derrubam os logins e as renovações no primeiro dia em produção.
5. **OPS-06 + OPS-10** — CI com PostgreSQL e verificação de migrações; migrações como passo próprio do deploy, com papel separado.
