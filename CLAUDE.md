# Kubico — contexto para o Claude Code

App para gerir a casa em família: casas (agregados) com vários membros, listas de compras e tarefas em tempo real, e mais tarde calendário, equipamentos e casa inteligente. A especificação completa do produto está no documento "App de Gestão Familiar e da Casa — Especificação" (Claude Docs).

## Estrutura

- `backend/` — API ASP.NET Core (.NET 10), minimal APIs.
  - `src/Kubico.Domain` — entidades e regras de negócio (ex.: `Chore.Complete`, recorrência e rotação).
  - `src/Kubico.Infrastructure` — `KubicoDbContext`, EF Core, migrações PostgreSQL em `Persistence/Migrations`.
  - `src/Kubico.Api` — endpoints por funcionalidade (`Auth/`, `Households/`, `Shopping/`, `Chores/`), SignalR em `Realtime/`.
  - `tests/Kubico.Api.Tests` — testes de integração com `WebApplicationFactory` + SQLite temporário e `FakeTimeProvider`.
- `app/` — Flutter (Android, iOS, Windows, macOS, Linux, web).
  - `lib/src/api` — `ApiClient` e modelos (espelham os DTOs da API).
  - `lib/src/session` — sessão e token (flutter_secure_storage).
  - `lib/src/realtime` — ligação SignalR e eventos.
  - `lib/src/ui` — ecrãs.

## Comandos

```bash
# Backend
cd backend
dotnet build -warnaserror
dotnet test
dotnet run --project src/Kubico.Api --launch-profile http        # http://localhost:5281
dotnet ef migrations add <Nome> --project src/Kubico.Infrastructure --startup-project src/Kubico.Api --output-dir Persistence/Migrations

# App
cd app
dart format lib test            # largura 120 (analysis_options.yaml)
flutter analyze
flutter test
KUBICO_API_URL=http://localhost:5281 flutter test test/live_api_test.dart   # contra a API a correr

# Tudo (PostgreSQL + API)
docker compose up --build
```

O CI (`.github/workflows/ci.yml`) corre build com `-warnaserror`, testes, `dart format --set-exit-if-changed`, `flutter analyze`, `flutter test` e `flutter build web`. Mantém tudo verde.

## Convenções

- **Tudo o que pertence à casa tem `HouseholdId`.** Os endpoints ficam em `/api/households/{householdId}/...` e começam sempre por `HouseholdAccess.RequireMemberAsync` (não-membros recebem 404, não 403).
- **Papéis**: Admin, Adult, Child, Guest. `CanManageContent()` (Admin/Adult) para criar/apagar conteúdo; `CanManageMembers()` (Admin) para convites, papéis e tirar membros. A casa tem sempre um administrador (`MemberEndpoints`). Tirar alguém passa pelo `MembershipService.RemoveAsync`, que arruma rotações, eventos, pedidos de recompensa, ligações ao Google/Outlook e avisos dessa casa; quando sai o último membro com conta, a casa é apagada.
- **Perfis de criança**: são `User` sem email, com `ManagedByHouseholdId` (a casa que os gere) e `PinHash`; o papel é sempre Child e não criam nem entram noutras casas. Entram em `/api/auth/child/login` com o código da família (`Household.ChildSignInCode`), o id do perfil e o PIN; 5 PINs errados bloqueiam o perfil 15 minutos (`User.RegisterWrongPin`). O `UserDto` tem `Email` nulo e `IsChildProfile`.
- **Tempo real**: cada alteração notifica o grupo da casa via `HouseholdNotifier` (`<Entidade>Upserted` / `<Entidade>Deleted`). Os nomes dos eventos têm de coincidir com `app/lib/src/realtime/household_realtime.dart`. Os clientes fazem upsert idempotente e ignoram versões mais antigas (`UpdatedAt`).
- **Datas**: instantes em `DateTimeOffset` UTC; prazos em `DateOnly`. "Hoje" calcula-se com `Household.Today(now)` (fuso da casa, por omissão Europe/Lisbon). Usa sempre `TimeProvider`, nunca `DateTime.Now`.
- **EF Core**: entidades com `Id` já preenchido adicionadas por navegação têm de ser registadas com `db.X.Add(...)`, senão o EF faz UPDATE em vez de INSERT.
- **Enums** viajam como texto em JSON (`JsonStringEnumConverter`) e na base de dados.
- **Calendário**: eventos com hora usam `StartsAt`/`EndsAt` (UTC); de dia inteiro, `StartDate`/`EndDate` (inclusive). A repetição é uma RRULE canónica (`RecurrenceRule`, subconjunto da RFC 5545) e as ocorrências, os conflitos e os feriados calculam-se no servidor, no fuso da casa (`GET /events?from&to`, até 92 dias). Dias apagados vão para `ExcludedDates`; um dia alterado é um evento próprio com `SeriesId`/`OriginalDate`. O feed ICS (`IcsWriter`) tem de refletir tudo isto (RRULE, EXDATE, RECURRENCE-ID).
- **Avisos**: cria-os com `NotificationService.NotifyAsync` (nunca vão para quem fez a alteração); chegam pelo canal pessoal do SignalR (`user:{id}`) e o `NotificationDispatcher` envia-os para os telemóveis segundo as preferências. O destino no FCM é o Firebase Installation ID (FID) que a app regista (`register()`/`onRegistered` do `firebase_messaging`), enviado no campo `fid`. A configuração Firebase da app (`lib/firebase_options.dart`, `google-services.json`, `web/firebase-messaging-sw.js`) é pública; a chave da conta de serviço nunca entra no repositório.
- **Google Calendar e Outlook** (`Calendar/Sync`): cada membro adulto liga a sua conta (OAuth com PKCE; o regresso é `GET /api/calendar-sync/callback/{google|microsoft}`, com quem é e de que casa no `state` cifrado). O Kubico cria lá um calendário próprio e só mexe nesse. `CalendarSyncService` lê primeiro o que mudou lá (Google: `syncToken`; Outlook: lista completa) e depois envia o que mudou no Kubico; `CalendarEventLink` guarda a cópia de cada evento e as versões dos dois lados, e ganha a alteração mais recente. Qualquer `CalendarEventUpserted`/`Deleted` do `HouseholdNotifier` pede uma sincronização da casa (`CalendarSyncTrigger`); o `CalendarSyncWorker` também passa por todas a cada 5 minutos. Os refresh tokens são cifrados com Data Protection (chaves na base de dados). Nos testes, os serviços são o `FakeCalendarProvider` e o worker está desligado.
- **Listas de compras na app**: as alterações passam pelo `ShoppingRepository` (cópia local + fila), não pelo `ApiClient` diretamente; os artigos novos levam o id gerado na app.
- **Sessão**: token de acesso de 15 minutos e refresh token rotativo (`SessionService`); a app renova sozinha num 401. Cada início de sessão é uma família de refresh tokens (o claim `sid` do token de acesso diz qual) e a app envia o tipo de dispositivo no cabeçalho `X-Kubico-Device`. Nos testes, os tokens de acesso duram 1 dia porque o relógio falso está à meia-noite — os testes que avançam o relógio usam um servidor próprio.
- **Erros** devolvem ProblemDetails com `title` em português, pronto a mostrar; a app mostra o `title` ou a primeira mensagem de validação.
- **Textos visíveis ao utilizador em português de Portugal.** Código, nomes de classes e commits técnicos podem ser em inglês; comentários em português.
- Cada funcionalidade nova leva testes de integração no backend (incluindo permissões e isolamento entre casas) e testes na app.

## Estado e próximos passos

Feito: contas (refresh tokens, lista de dispositivos com sessão, terminar sessão num ou em todos), casas e convites, gestão de membros (papéis, tirar, sair, passar a administração), perfis de criança com PIN, listas de compras em tempo real e sem rede, tarefas recorrentes com rotação e pontos, recompensas, calendário partilhado (repetições e exceções, boleias, conflitos, feriados, subscrição ICS, sync nos dois sentidos com Google Calendar e Outlook), avisos (na app e no telemóvel via FCM, projeto Firebase `kubico-casa`, Android e web), app em pt-PT.

A seguir (por esta ordem, salvo indicação em contrário):
1. Avisos no iPhone (precisa de conta Apple Developer e da chave APNs no Firebase).
2. Sync do calendário na hora (notificações push do Google e do Microsoft Graph), em vez de a cada 5 minutos; precisa da API num endereço público.
