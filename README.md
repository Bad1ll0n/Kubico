# Kubico

App para gerir tudo o que é de casa: famílias com várias contas, calendário partilhado, tarefas, listas de compras, equipamentos e casa inteligente.

> "Cubico" é como se diz casa em Angola.

## O que já funciona

- **Contas**: registo e entrada com email e palavra-passe. A sessão renova-se sozinha (token de acesso de 15 minutos e refresh token rotativo de 60 dias) e pode terminar-se em todos os dispositivos.
- **Casas (agregados)**: criar uma casa, convidar membros com um código, papéis (Administrador, Adulto, Criança, Convidado), cor por membro, várias casas por utilizador.
- **Listas de compras partilhadas em tempo real**: criar listas, adicionar artigos ("2 leite" separa a quantidade), marcar como comprado, limpar o carrinho. Quem tem a mesma lista aberta vê as alterações na hora (SignalR). **Funcionam sem rede**: a app guarda uma cópia e as alterações feitas no supermercado sem ligação são enviadas quando a rede voltar, sem duplicar artigos.
- **Tarefas e rotinas**: tarefas únicas ou recorrentes (diárias, semanais, mensais), responsável ou "qualquer pessoa", rotação automática entre membros a cada conclusão, pontos e resumo dos últimos 30 dias por membro. As crianças só concluem as suas tarefas ou as de qualquer pessoa. Tarefas em atraso saltam para o período seguinte sem acumular.
- **Calendário partilhado**: vistas de dia, semana e mês; eventos com hora ou de dia inteiro (vários dias incluídos), com repetição diária, semanal (em vários dias da semana), mensal ou anual, até uma data. Cada evento tem participantes (com a cor de cada membro) e boleias: quem leva e quem vai buscar. A app avisa quando alguém está em dois eventos ao mesmo tempo ou tem de levar/ir buscar alguém enquanto está noutro compromisso. Mostra os feriados nacionais de Portugal. A mudança de hora não desloca as repetições (às 18:00 continua às 18:00). Num evento que se repete, dá para alterar ou apagar só esse dia, esse e os seguintes, ou todos. Cada membro tem um endereço pessoal para ver o calendário da casa no Google Calendar, no Outlook ou no iPhone (iCalendar).
- **Recompensas**: os adultos criam recompensas com um custo em pontos; os pontos ganham-se nas tarefas. Os pedidos das crianças esperam a aprovação de um adulto (os pontos ficam reservados e voltam se o pedido for recusado ou cancelado).
- **Avisos**: quando uma tarefa passa a ser tua, quando te põem num evento ou numa boleia, e nos pedidos de recompensas. Aparecem logo na app (sino com o número por ler) e seguem para o telemóvel conforme as preferências de cada um: logo, num resumo diário ou só na app, com horas de silêncio.
- **App Flutter** para Android, iOS, Windows, macOS, Linux e web, em português de Portugal (datas, horas de 24 horas). Em ecrãs largos (PC, tablet) mostra as listas e os artigos lado a lado.

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
cd backend && dotnet test        # 95 testes (integração e regras de negócio)
cd app && flutter test           # 39 testes unitários e de widgets
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
- `PublicBaseUrl` (opcional): endereço público da API (ex.: `https://api.kubico.pt`), para os endereços de subscrição do calendário quando a API está atrás de um proxy.
- `Push__Fcm__ServiceAccountJson` (opcional): o JSON da conta de serviço do Firebase, para os avisos chegarem aos telemóveis. Sem ele, os avisos ficam só na app.
- `Jwt__AccessTokenLifetimeMinutes` (15) e `Jwt__RefreshTokenLifetimeDays` (60), se quiseres outras durações de sessão.

## Próximos passos

1. Avisos no telemóvel: criar o projeto Firebase, correr `flutterfire configure` e juntar o `firebase_messaging` à app, que regista o token em `POST /api/notifications/devices`. O servidor já envia pelo FCM.
2. Calendário: sync nos dois sentidos com Google e Outlook (precisa de credenciais OAuth da Google e da Microsoft). Por agora, o calendário da casa vê-se nessas apps através do endereço de subscrição.
3. Lista de dispositivos com sessão iniciada (os refresh tokens já guardam o dispositivo).

A especificação completa do produto está no documento "App de Gestão Familiar e da Casa — Especificação".
