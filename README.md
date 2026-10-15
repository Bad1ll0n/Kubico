# Kubico

App para gerir tudo o que é de casa: famílias com várias contas, calendário partilhado, tarefas, listas de compras, equipamentos e casa inteligente.

> "Cubico" é como se diz casa em Angola.

## O que já funciona

Primeira fatia vertical, de ponta a ponta:

- **Contas**: registo e entrada com email e palavra-passe (JWT).
- **Casas (agregados)**: criar uma casa, convidar membros com um código, papéis (Administrador, Adulto, Criança, Convidado), cor por membro, várias casas por utilizador.
- **Listas de compras partilhadas em tempo real**: criar listas, adicionar artigos ("2 leite" separa a quantidade), marcar como comprado, limpar o carrinho. Quem tem a mesma lista aberta vê as alterações na hora (SignalR).
- **Tarefas e rotinas**: tarefas únicas ou recorrentes (diárias, semanais, mensais), responsável ou "qualquer pessoa", rotação automática entre membros a cada conclusão, pontos e resumo dos últimos 30 dias por membro. As crianças só concluem as suas tarefas ou as de qualquer pessoa. Tarefas em atraso saltam para o período seguinte sem acumular.
- **App Flutter** para Android, iOS, Windows, macOS, Linux e web. Em ecrãs largos (PC, tablet) mostra as listas e os artigos lado a lado.

## Estrutura

```
backend/                 API ASP.NET Core (.NET 10)
  src/Kubico.Domain          entidades
  src/Kubico.Infrastructure  EF Core, PostgreSQL, migrações
  src/Kubico.Api             endpoints, autenticação, SignalR
  tests/Kubico.Api.Tests     testes de integração (SQLite, sem Docker)
app/                     app Flutter
  lib/src/api                cliente HTTP e modelos
  lib/src/session            sessão e token
  lib/src/realtime           ligação SignalR
  lib/src/ui                 ecrãs
docker-compose.yml       PostgreSQL + API para desenvolvimento
```

## Correr localmente

### 1. API

Com Docker (mais simples):

```bash
docker compose up --build
```

Sem Docker, com um PostgreSQL local (utilizador/palavra-passe/base de dados `kubico`):

```bash
cd backend
dotnet run --project src/Kubico.Api --launch-profile http
```

A API fica em `http://localhost:5281`. Em desenvolvimento as migrações aplicam-se sozinhas e o OpenAPI está em `/openapi/v1.json`.

### 2. App

```bash
cd app
flutter pub get
flutter run -d windows   # ou chrome, macos, linux, ou um telemóvel/emulador
```

Por omissão a app liga-se a `http://localhost:5281` (no emulador Android, `http://10.0.2.2:5281`). Para outra API:

```bash
flutter run --dart-define=API_URL=https://api.exemplo.pt
```

## Testes

```bash
cd backend && dotnet test        # 31 testes (integração e regras de negócio)
cd app && flutter test           # testes unitários e de widgets
```

Teste da app contra uma API real (tempo real incluído):

```bash
KUBICO_API_URL=http://localhost:5281 flutter test test/live_api_test.dart
```

## Produção: o que configurar

- `Jwt__SigningKey`: chave secreta com pelo menos 32 caracteres (nunca a de desenvolvimento).
- `ConnectionStrings__Kubico`: ligação ao PostgreSQL.
- `Cors__AllowedOrigins__0`: domínio da app web.
- Aplicar migrações no deploy (`Database__MigrateOnStartup=true` ou `dotnet ef database update`).

## Próximos passos

1. Calendário partilhado (eventos da casa; depois sync Google/Outlook).
2. Recompensas trocáveis por pontos.
3. Modo offline na lista de compras (cache local e fila de alterações).
4. Refresh tokens e "terminar sessão em todos os dispositivos".
5. Notificações push.

A especificação completa do produto está no documento "App de Gestão Familiar e da Casa — Especificação".
