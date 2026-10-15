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
- **Papéis**: Admin, Adult, Child, Guest. `CanManageContent()` (Admin/Adult) para criar/apagar conteúdo; `CanManageMembers()` (Admin) para convites.
- **Tempo real**: cada alteração notifica o grupo da casa via `HouseholdNotifier` (`<Entidade>Upserted` / `<Entidade>Deleted`). Os nomes dos eventos têm de coincidir com `app/lib/src/realtime/household_realtime.dart`. Os clientes fazem upsert idempotente e ignoram versões mais antigas (`UpdatedAt`).
- **Datas**: instantes em `DateTimeOffset` UTC; prazos em `DateOnly`. "Hoje" calcula-se com `Household.Today(now)` (fuso da casa, por omissão Europe/Lisbon). Usa sempre `TimeProvider`, nunca `DateTime.Now`.
- **EF Core**: entidades com `Id` já preenchido adicionadas por navegação têm de ser registadas com `db.X.Add(...)`, senão o EF faz UPDATE em vez de INSERT.
- **Enums** viajam como texto em JSON (`JsonStringEnumConverter`) e na base de dados.
- **Calendário**: eventos com hora usam `StartsAt`/`EndsAt` (UTC); de dia inteiro, `StartDate`/`EndDate` (inclusive). A repetição é uma RRULE canónica (`RecurrenceRule`, subconjunto da RFC 5545) e as ocorrências, os conflitos e os feriados calculam-se no servidor, no fuso da casa (`GET /events?from&to`, até 92 dias).
- **Erros** devolvem ProblemDetails com `title` em português, pronto a mostrar; a app mostra o `title` ou a primeira mensagem de validação.
- **Textos visíveis ao utilizador em português de Portugal.** Código, nomes de classes e commits técnicos podem ser em inglês; comentários em português.
- Cada funcionalidade nova leva testes de integração no backend (incluindo permissões e isolamento entre casas) e testes na app.

## Estado e próximos passos

Feito: contas (JWT), casas e convites, listas de compras em tempo real, tarefas recorrentes com rotação e pontos, calendário partilhado (repetições, boleias, conflitos, feriados).

A seguir (por esta ordem, salvo indicação em contrário):
1. Calendário: sync Google/Outlook e exceções em eventos que se repetem (editar ou apagar só uma ocorrência).
2. Recompensas trocáveis por pontos.
3. Modo offline na lista de compras.
4. Refresh tokens.
5. Notificações push.
