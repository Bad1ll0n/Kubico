# Kubico

App para gerir tudo o que é de casa: famílias com várias contas, calendário partilhado, tarefas, listas de compras, equipamentos e casa inteligente.

> "Cubico" é como se diz casa em Angola.

## O que já funciona

- **Contas**: registo e entrada com email e palavra-passe. A sessão renova-se sozinha (token de acesso de 15 minutos e refresh token rotativo de 60 dias) e pode terminar-se em todos os dispositivos, ou só num, a partir da lista de dispositivos com sessão iniciada.
- **Casas (agregados)**: criar uma casa, convidar membros com um código, papéis (Administrador, Adulto, Criança, Convidado), cor por membro, várias casas por utilizador. O administrador muda papéis, tira pessoas da casa e pode passar a administração a outra pessoa; qualquer membro pode sair. Quem sai deixa as rotações e os eventos, e as suas tarefas passam a quem vem a seguir; o histórico fica. A casa tem sempre um administrador, e é apagada quando sai o último membro.
- **Perfis de criança com PIN**: um adulto cria um perfil para cada criança, sem email. No telemóvel ou tablet dela, a criança escreve o código da família, escolhe o seu nome e escreve o PIN (4 a 6 algarismos). Ao fim de 5 PINs errados, o perfil fica bloqueado 15 minutos; um adulto pode mudar o PIN e gerar um código da família novo.
- **Listas de compras partilhadas em tempo real**: criar listas, adicionar artigos ("2 leite" separa a quantidade), marcar como comprado, limpar o carrinho. Quem tem a mesma lista aberta vê as alterações na hora (SignalR). **Funcionam sem rede**: a app guarda uma cópia e as alterações feitas no supermercado sem ligação são enviadas quando a rede voltar, sem duplicar artigos.
- **Tarefas e rotinas**: tarefas únicas ou recorrentes (diárias, semanais, mensais), responsável ou "qualquer pessoa", rotação automática entre membros a cada conclusão, pontos e resumo dos últimos 30 dias por membro. As crianças só concluem as suas tarefas ou as de qualquer pessoa. Tarefas em atraso saltam para o período seguinte sem acumular.
- **Calendário partilhado**: vistas de dia, semana e mês; eventos com hora ou de dia inteiro (vários dias incluídos), com repetição diária, semanal (em vários dias da semana), mensal ou anual, até uma data. Cada evento tem participantes (com a cor de cada membro) e boleias: quem leva e quem vai buscar. A app avisa quando alguém está em dois eventos ao mesmo tempo ou tem de levar/ir buscar alguém enquanto está noutro compromisso. Mostra os feriados nacionais de Portugal. A mudança de hora não desloca as repetições (às 18:00 continua às 18:00). Num evento que se repete, dá para alterar ou apagar só esse dia, esse e os seguintes, ou todos. Cada membro tem um endereço pessoal para ver o calendário da casa no Google Calendar, no Outlook ou no iPhone (iCalendar).
- **Google Calendar e Outlook nos dois sentidos**: cada adulto liga a sua conta Google ou Microsoft. O Kubico cria lá um calendário próprio («Kubico · Casa») com os eventos da casa, e o que se mudar nesse calendário volta ao Kubico em poucos minutos (eventos novos, alterados ou apagados, e no Google também dias alterados ou apagados de uma série). Quando os dois lados mudam o mesmo evento, fica a alteração mais recente. Os outros calendários da pessoa não são lidos nem alterados.
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
cd backend && dotnet test        # 123 testes (integração e regras de negócio)
cd app && flutter test           # 54 testes unitários e de widgets
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
- `Push__Fcm__ServiceAccountFile` (ou `Push__Fcm__ServiceAccountJson`, com o conteúdo): a chave da conta de serviço do projeto Firebase `kubico-casa`, para os avisos chegarem aos telemóveis. Sem ela, os avisos ficam só na app. Em desenvolvimento: `dotnet user-secrets set "Push:Fcm:ServiceAccountFile" "C:\Users\Badillon\segredos\kubico-firebase.json" --project backend/src/Kubico.Api`.
- `Jwt__AccessTokenLifetimeMinutes` (15) e `Jwt__RefreshTokenLifetimeDays` (60), se quiseres outras durações de sessão.
- `CalendarSync__Google__ClientId`/`ClientSecret` e `CalendarSync__Microsoft__ClientId`/`ClientSecret`: as credenciais OAuth para ligar contas Google e Microsoft (ver abaixo). Sem elas, o serviço aparece como indisponível na app. Os endereços de regresso a registar são `<PublicBaseUrl>/api/calendar-sync/callback/google` e `.../callback/microsoft`. Os tokens das contas ficam cifrados com chaves guardadas na própria base de dados (tabela `DataProtectionKeys`).

## Ligar ao Google Calendar e ao Outlook

As credenciais são da app Kubico (não de cada pessoa). O segredo de cada uma nunca entra no repositório: em desenvolvimento vai para os user-secrets, em produção para variáveis de ambiente.

**Google** (pode ser no projeto `kubico-casa`, que também é um projeto Google Cloud), em [console.cloud.google.com](https://console.cloud.google.com):

1. APIs e serviços → Biblioteca → ativar a **Google Calendar API**.
2. Google Auth Platform → Branding: nome "Kubico" e email de apoio. Público: **Externo**. Acesso a dados: adicionar o âmbito `.../auth/calendar.app.created` (só os calendários que o Kubico cria).
3. Clientes → Criar cliente → **Aplicação Web**, com o URI de redirecionamento `http://localhost:5281/api/calendar-sync/callback/google` (e o de produção).
4. Enquanto a app estiver "Em teste", só entram as contas da lista de utilizadores de teste e as ligações caducam ao fim de 7 dias; para uso real, publica a app (aparece o aviso de app não verificada até a Google a verificar).

**Microsoft**, em [portal.azure.com](https://portal.azure.com) → Microsoft Entra ID → Registos de aplicações → Novo registo:

1. Nome "Kubico"; tipos de conta: **contas de qualquer diretório organizacional e contas Microsoft pessoais**.
2. URI de redirecionamento (Web): `http://localhost:5281/api/calendar-sync/callback/microsoft` (e o de produção).
3. Certificados e segredos → Novo segredo do cliente (copia o **Valor**). Permissões de API: Microsoft Graph, delegadas, `Calendars.ReadWrite` e `offline_access`.

Depois, em desenvolvimento:

```bash
dotnet user-secrets set "CalendarSync:Google:ClientId" "<id do cliente>" --project backend/src/Kubico.Api
dotnet user-secrets set "CalendarSync:Google:ClientSecret" "<segredo>" --project backend/src/Kubico.Api
dotnet user-secrets set "CalendarSync:Microsoft:ClientId" "<id da aplicação (cliente)>" --project backend/src/Kubico.Api
dotnet user-secrets set "CalendarSync:Microsoft:ClientSecret" "<valor do segredo>" --project backend/src/Kubico.Api
```

O navegador tem de conseguir chegar à API no endereço de regresso: com `localhost`, só funciona no mesmo computador (app web ou Windows). Para ligar contas a partir do telemóvel, a API tem de estar num endereço público em HTTPS (`PublicBaseUrl`), registado no Google e na Microsoft.

## Próximos passos

1. Avisos no iPhone (precisa de conta Apple Developer e da chave APNs no Firebase).
2. Calendário: receber as alterações do Google e do Outlook na hora (notificações push dos dois serviços, que precisam da API num endereço público), em vez de verificar a cada 5 minutos.

A especificação completa do produto está no documento "App de Gestão Familiar e da Casa — Especificação".
