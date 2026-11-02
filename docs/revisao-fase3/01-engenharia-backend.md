# Kubico — Revisão fase 3: engenharia do backend

Revisor: engenharia backend (.NET/EF Core/PostgreSQL/SignalR). Branch `mvp-continuacao` @ 60bb5a1.
Método: leitura de todo o `backend/src` (Domain, Infrastructure, Api), sem correr build nem testes (outro revisor está a corrê-los). Caminhos relativos a `backend/src/`.

Não encontrei nada que mereça **Crítico** (perda de dados irrecuperável ou falha de segurança explorável sem condições). Há 6 problemas **Altos**: dados de pontos e histórico, tempo real depois de desfazer, e uma lacuna de testes que esconde problemas de PostgreSQL.

---

## Alto

### ENG-01 — Apagar uma tarefa apaga os pontos, as estatísticas e a base da mesada, sem aviso
- **Onde:** `Kubico.Api/Chores/ChoreEndpoints.cs:242-247` (DeleteChore); cascata em `Kubico.Infrastructure/Persistence/KubicoDbContext.cs:259`; o saldo é calculado em `Kubico.Api/Rewards/Points.cs:35-44`.
- **Cenário:** a Rita faz "Pôr a mesa" todos os dias há 3 meses (300 pts) e troca 250 pts por recompensas. Um adulto apaga a tarefa porque deixou de fazer sentido. Com ela vão as `ChoreCompletion` (cascata): o saldo da Rita passa para −250 ("deve 250 pts"), as estatísticas perdem 3 meses e a mesada dos períodos ainda por pagar baixa (o extra por tarefa conta as conclusões). O `DeleteChore` não chama `Points.LockAsync`/`SpentProblem`, ao contrário de rejeitar e de desfazer. Desfazer só serve durante 30 dias. Depois disso, a perda é definitiva.
- **Correção:** apagar uma tarefa com conclusões passa a arquivá-la (`ArchivedAt`, fica fora das listas, as conclusões ficam). Se se quiser mesmo apagar de vez, faz-se primeiro a verificação de pontos (409 `points-spent` com `force`). A médio prazo, ver ENG-18 (livro de pontos).

### ENG-02 — Desfazer repõe o `UpdatedAt` antigo, e os outros aparelhos ignoram a mudança
- **Onde:** `Kubico.Api/Activity/ActivityLog.cs:104-124`. O snapshot (`Before`, linha 188) guarda todas as propriedades, e o ciclo de reposição só salta a chave e o token de concorrência. Do lado da app: `app/lib/src/ui/shopping_items_view.dart:132`, `chores_view.dart:132` e `calendar_view.dart:208` ignoram versões com `updatedAt` mais antigo.
- **Cenário:** a Ana muda «Leite» para «Leite magro» (UpdatedAt T2). O Rui desfaz. O servidor repõe o nome e também `UpdatedAt = T1` e envia `ShoppingItemUpserted`. Os telemóveis que já tinham T2 descartam o evento porque T1 < T2, e continuam a mostrar «Leite magro» até recarregarem a lista. O mesmo acontece com tarefas, eventos (o calendário nem recarrega) e recompensas. Isto contraria a regra "os clientes ignoram versões mais antigas".
- **Correção:** no `UndoAsync`, depois de repor, mete-se `UpdatedAt = clock.GetUtcNow()` em todas as entidades com essa propriedade (repostas ou recriadas). Falta um teste: "desfazer → o evento enviado tem UpdatedAt > o anterior".

### ENG-03 — "Não aceitar" uma conclusão antiga faz recuar a tarefa, e as semanais mudam de dia
- **Onde:** `Kubico.Api/Chores/ChoreHistoryEndpoints.cs:188-217` e `Kubico.Domain/Chores/Chore.cs:84-100` (`Reopen`, `DueDate = today`).
- **Cenários:**
  1. Numa tarefa diária com rotação, a Ana fez ontem e o Rui fez hoje (o prazo já está em amanhã e é a vez da Sara). O adulto não aceita a conclusão de *ontem* da Ana. O `Reopen` põe a tarefa para hoje e atribuída à Ana. A conclusão do Rui de hoje continua a contar, mas a ocorrência de hoje volta a aparecer e a rotação recua. O endpoint aceita conclusões de qualquer idade (não há limite nem verificação de que é a última).
  2. Numa semanal à segunda-feira, uma rejeição à quarta põe `DueDate = quarta`. A conclusão seguinte calcula `quarta + 7`, e a tarefa passa a ser à quarta para sempre. As mensais não sofrem com isto porque usam `DueDay`.
- **Correção:** só reabrir quando a conclusão rejeitada é a mais recente da tarefa (nas outras, tiram-se só os pontos). Ao reabrir uma recorrente, repor o prazo da ocorrência que foi rejeitada (o `Previous` do prazo atual), não "hoje". Também convém limitar a rejeição a um período (por exemplo, o do histórico, 30 dias).

### ENG-04 — Violações de índice único dão 500 em vários sítios (a regra "nunca 500" não se cumpre)
- **Onde:** `Kubico.Api/Common/ApiExceptionHandler.cs:22-27` só trata `DbUpdateConcurrencyException`. Um `DbUpdateException` de chave duplicada chega ao cliente como 500 em:
  - `Auth/AuthEndpoints.cs:77-94`: dois registos com o mesmo email ao mesmo tempo (o `AnyAsync` antes não protege).
  - `Notifications/NotificationEndpoints.cs:137-160`: `RegisterDevice` com o mesmo token FCM em paralelo. A app regista ao arrancar e no `onTokenRefresh`, por isso isto acontece na prática.
  - `Shopping/ProductEndpoints.cs:133-145`: duas casas a ler o mesmo código de barras novo ao mesmo tempo (PK `ProductInfo.Barcode`).
  - `Shopping/ProductEndpoints.cs:156-166` (`RememberAsync`): dois artigos com o mesmo código na mesma casa. Em `ShoppingEndpoints.cs:254`, o `catch` só cobre o id repetido do artigo, por isso relança e dá 500.
  - `Activity/ActivityEndpoints.cs:191-207`: dois "desfazer" em paralelo de uma eliminação reinserem o mesmo Id.
  - `Households/PermissionEndpoints.cs:91-123`: duas gravações de permissões em paralelo.
- **Correção:** (a) no `ApiExceptionHandler`, tratar `DbUpdateException` com violação de unicidade (`PostgresException.SqlState == "23505"`, `SqliteException.SqliteErrorCode == 19`) como 409 `ChangedMeanwhile`; (b) nos casos idempotentes (dispositivo, produto), usar um upsert (`INSERT … ON CONFLICT`) ou apanhar a exceção e reler; (c) no undo, marcar a entrada como desfeita com um `ExecuteUpdate` condicional (`WHERE UndoneAt IS NULL`) antes de aplicar.

### ENG-05 — Os testes só correm em SQLite: as migrações e o comportamento de PostgreSQL nunca são testados
- **Onde:** `tests/Kubico.Api.Tests` (nenhuma referência a PostgreSQL ou Testcontainers), `.github/workflows/ci.yml` (sem serviço postgres), `Program.cs:252-259` (em SQLite usa-se `EnsureCreated`, não as migrações).
- **Porque é Alto:** o CLAUDE.md exige "tem de funcionar em SQLite e em PostgreSQL", e a produção é PostgreSQL. O SQLite serializa todas as escritas (um trinco para a base inteira), por isso os `ConcurrencyTests` não exercitam o MVCC/READ COMMITTED do PostgreSQL, onde aparecem os problemas de ENG-04 e ENG-09. As coleções primitivas (`List<Guid>` de rotações e participantes, `List<DateOnly>` de `ExcludedDates`) são `uuid[]`/`date[]` no Npgsql e JSON no SQLite: são traduções diferentes que nenhum teste cobre. Uma migração esquecida ou mal gerada só se descobre em produção. O aviso Skip/Take (ENG-07) também só foi visto em Production.
- **Correção:** no CI, um segundo job com `services: postgres` que corre a suite (ou pelo menos `ConcurrencyTests`, `CalendarTests`, `ChoreTests`) com `Database:Provider=Postgres` e `MigrateAsync`. Juntar também `dotnet ef migrations has-pending-model-changes` para apanhar o modelo à frente das migrações.

### ENG-06 — Desfazer repõe referências que já não existem: FK partida (500) ou ex-membros de volta
- **Onde:** `Kubico.Api/Activity/ActivityLog.cs:104-124` (repõe os valores sem os validar); `Kubico.Api/Households/MemberEndpoints.cs:113` e `:162` (o `Capture` acontece *antes* do `MembershipService.RemoveAsync`, por isso as tarefas e os eventos mudados pela saída do membro não ficam nas `EntityKeys`).
- **Cenários:**
  1. Apaga-se uma tarefa com fotos de prova. As conclusões saem, e as fotos ficam "não usadas": ao fim de 1 dia o `CleanUpAsync` (`ChoreHistoryEndpoints.cs:242-259`) apaga-as. Um desfazer nos 30 dias seguintes reinsere conclusões com `PhotoId` de fotos que já não existem. Resultado: violação de FK, `DbUpdateException` e 500. Mesmo que não falhasse, as fotos já se tinham perdido.
  2. O admin tira o Rui da casa: ele sai das rotações e dos eventos. Uma entrada anterior "mudou a tarefa X" continua a ser a "última" de X (a remoção não ficou registada contra X). Desfazê-la volta a pôr o Rui como responsável e na rotação, sem ser membro, e ele passa a receber avisos de tarefas de uma casa onde já não está.
- **Correção:** chamar `activity.Capture` depois de `RemoveAsync` aplicar as mudanças (ou fazer o `RemoveAsync` registar as suas próprias chaves). No `UndoAsync`, validar antes de gravar: tirar das listas os ids de quem já não é membro, pôr `PhotoId` a null se a foto não existir, e recusar com 409 se faltar a entidade-mãe. As fotos de conclusões apagadas devem durar tanto como o histórico (30 dias).

---

## Médio

### ENG-07 — O aviso "Skip/Take sem OrderBy": `NotificationDispatcher`, a cada minuto
- **Onde:** `Kubico.Api/Notifications/NotificationDispatcher.cs:43-50`:
  ```csharp
  var users = await db.Notifications.AsNoTracking()
      .Where(n => n.PushedAt == null)
      .Where(n => !db.NotificationPreferences.Any(p => p.UserId == n.UserId && p.NextPushAt > now))
      .Select(n => n.UserId)
      .Distinct()
      .Where(id => !seen.Contains(id))
      .Take(BatchSize)          // ← sem OrderBy
      .ToListAsync(ct);
  ```
  É a única consulta com `Take`/`Skip` sem ordenação (as de `Maintenance.cs:50`, `ActivityEndpoints.cs:72`, `ChoreHistoryEndpoints.cs:154`, `NotificationEndpoints.cs:55` e `RewardEndpoints.cs:97` têm `OrderBy`). Corre a cada minuto no `NotificationWorker`, por isso o aviso repete-se no log de Production.
- **Efeito:** a correção não está em risco, porque o `seen` impede que alguém seja tratado duas vezes. Mas o lote não é determinista, a lista `seen` cresce até 10 000 parâmetros (`NOT IN`/`<> ALL`) e o log fica cheio de ruído.
- **Correção:** paginação por chave, sem `seen`: `… .Select(n => n.UserId).Distinct().Where(id => id > lastId).OrderBy(id => id).Take(BatchSize)`, guardando `lastId = users[^1]` entre lotes. Opcionalmente, trocar `ConfigureWarnings(w => w.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))` nos testes para que o próximo caso apareça no CI.

### ENG-08 — Calendário, "esta e as seguintes": os dias apagados voltam e as alterações de dias seguintes perdem-se
- **Onde:** `Kubico.Domain/Calendar/CalendarEvent.cs:249` (`EndBefore` tira à série antiga os `ExcludedDates >= date`); `Kubico.Api/Calendar/CalendarEndpoints.cs:361-372` (a nova série nasce sem `ExcludedDates`, e `RemoveExceptions(d >= date)` apaga as exceções).
- **Cenário:** natação todas as terças. O dia 18/11 foi apagado ("só este") e o 25/11 mudou de hora. A 4/11, muda-se o local "desta e das seguintes". A nova série volta a ter o 18/11, e o 25/11 volta à hora antiga. A Google/Outlook recebe tudo isto.
- **Correção:** passar para a nova série os `ExcludedDates >= date` e voltar a ligar as exceções `>= date` (`SeriesId = nova.Id`), em vez de as apagar (quando a regra e a hora não mudam). Juntar o caso aos `CalendarEquivalenceTests`.

### ENG-09 — "A casa tem sempre um administrador" e "o último a sair apaga a casa" falham com pedidos em simultâneo (write skew)
- **Onde:** `Kubico.Api/Households/MemberEndpoints.cs:72-74` (`AdminCount`), `:148-163` (`Leave`), `Auth/AccountEndpoints.cs:343-375`.
- **Cenário:** dois administradores despromovem-se um ao outro, ou saem os dois, ao mesmo tempo. Cada um vê `AdminCount == 2` e muda uma linha *diferente*: a versão de cada `Membership` não colide, e a casa fica sem administrador. Do mesmo modo, os dois últimos adultos saem em paralelo e cada um vê `others.Count == 1`. A casa fica sem contas, com os perfis de criança órfãos e sem ser apagada.
- **Correção:** serializar por casa com um cadeado. Por exemplo, pôr `Version` (`IVersioned`) em `Household` e fazer `db.Touch(household)` em todas as operações que mudam papéis ou membros, como no `Points.LockAsync`. Prova-se com um teste em paralelo no PostgreSQL (ENG-05).

### ENG-10 — Efeitos depois do commit com o `CancellationToken` do pedido e sem outbox
- **Onde:** é o padrão em todos os endpoints (ex.: `ChoreEndpoints.cs:473-477`, `ChoreHistoryEndpoints.cs:219-231`, `RewardEndpoints.cs:272-290`). O `NotificationService.NotifyAsync` faz um segundo `SaveChangesAsync` (`NotificationService.cs:66`).
- **Cenário:** o telemóvel perde a rede logo depois do commit. O `ct` é cancelado, o primeiro `SendAsync` lança `OperationCanceledException`, e o `PointsChanged`, o aviso "É a tua vez" e o `NotificationCreated` não chegam a sair. Os dados ficam gravados mas a casa não é avisada. O mesmo acontece se o segundo `SaveChanges` (avisos) falhar: a ação fica feita e o aviso perde-se, sem nova tentativa.
- **Correção (rápida):** depois do commit, usar `CancellationToken.None` (ou um token com 5 s) para as notificações. **Estrutural (V2):** eventos de domínio recolhidos antes do `SaveChanges` e gravados numa tabela outbox na mesma transação, com um worker a publicar SignalR/avisos/sync. Isto também acaba com os efeitos que se repetem à mão em cada endpoint.

### ENG-11 — Mesada: mudar de semanal para mensal esquece o que estava por pagar; valores retroativos
- **Onde:** `Kubico.Api/Rewards/AllowanceEndpoints.cs:131-135` e `:401-417`.
- **Cenário:** 3 semanas por pagar (15 €). O adulto muda para mensal. `StartDate` passa a ser o dia 1 do mês atual, os períodos anteriores deixam de ser listados e os 15 € em `OwedCents` desaparecem. Além disso, os períodos não pagos são sempre calculados com o `AmountCents`/`BonusPerChoreCents` *atuais*: baixar o valor hoje reduz o que se devia pelas semanas passadas.
- **Correção:** antes de mudar o período ou o valor, fechar os períodos acabados e não pagos (por exemplo, criar `AllowancePeriod`s "por pagar" com o valor do momento) ou recusar com 409 "paga ou anula primeiro os períodos em atraso". Guardar o histórico de valores (`AllowanceRate` com `ValidFrom`).

### ENG-12 — Apagar a conta não é atómico
- **Onde:** `Kubico.Api/Auth/AccountEndpoints.cs:417-450`. Há um `SaveChanges` por casa, chamadas externas (Google/Outlook) pelo meio, e só no fim o `ExecuteDelete` do utilizador.
- **Cenário:** um 409 de concorrência (ou um timeout) na 2.ª de 3 casas: a pessoa já saiu de uma, a administração de outra já passou, e a conta continua a existir. A app mostra um erro e não há forma de voltar atrás.
- **Correção:** fazer as chamadas externas primeiro (ou depois, como "melhor esforço" numa fila) e meter as mudanças na base numa transação (`BeginTransactionAsync` com todas as remoções e o `ExecuteDelete` final). O `MembershipService` precisa de uma variante sem `SaveChanges` interno.

### ENG-13 — Sincronização do calendário: lê a casa toda em cada volta e pode pisar edições feitas em simultâneo
- **Onde:** `Kubico.Api/Calendar/Sync/CalendarSyncService.cs:196-201` (todos os `CalendarEvents` da casa, com tracking, em cada sincronização: 5 em 5 minutos por ligação e a cada alteração); `CalendarEvent` não implementa `IVersioned`.
- **Cenário:** o sync lê os eventos, fala com a Google durante segundos e grava. Se, nesse intervalo, um adulto muda a hora de um evento que também mudou na Google, a gravação do sync pisa a alteração sem conflito (last-writer-wins à revelia do `KubicoWins`). As mudanças vindas do sync também não ficam no `ActivityLog`, por isso um desfazer posterior pode pisá-las sem aviso (ver ENG-06). Com anos de histórico e várias ligações por casa, cada volta carrega e segue milhares de entidades.
- **Correção:** `CalendarEvent : IVersioned`, para que o sync apanhe `DbUpdateConcurrencyException` e repita na volta seguinte. Carregar só os eventos ligados (`CalendarEventLinks`) ou alterados desde `LastSyncedAt` (`UpdatedAt >= …`) mais os referidos pelas alterações remotas. Registar as alterações do sync no histórico ("Google Calendar mudou …", não desfazíveis, mas com `EntityKeys`).

### ENG-14 — `MembershipService.RemoveAsync` lê todas as tarefas e eventos da casa e fala com serviços externos antes do commit
- **Onde:** `Kubico.Api/Households/MembershipService.cs:34-39` e `:46`.
- **Problema:** para tirar uma pessoa, lê-se (com tracking) *todas* as tarefas e *todos* os eventos da casa, e filtra-se em memória. Depois apaga-se o calendário remoto (até 10 s por ligação) e só então se grava: se a gravação falhar, a cópia externa já foi apagada.
- **Correção:** filtrar na base (`c.AssigneeUserId == userId || c.RotationUserIds.Contains(userId)`, que funciona com coleções primitivas nos dois fornecedores, ver ENG-05) e fazer a limpeza remota depois do commit (ou numa fila).

### ENG-15 — Editar uma tarefa já feita para se repetir deixa-a escondida para sempre
- **Onde:** `Kubico.Api/Chores/ChoreEndpoints.cs:599-622` (`Apply` nunca mexe em `CompletedAt`) e `:173-216` (o `UpdateChore` não verifica `IsDone`).
- **Cenário:** "Trocar a lâmpada" (única) foi feita. Alguém abre "feitas", muda para semanal e grava. Como `CompletedAt` continua preenchido, a tarefa fica `IsDone`, desaparece da lista e qualquer conclusão dá 409 "já está concluída".
- **Correção:** no `Apply`, se `Recurrence != None`, pôr `CompletedAt = null`. Ou recusar a edição de tarefas concluídas, salvo para as reabrir explicitamente.

### ENG-16 — Arquitetura: a Api é um conjunto de "transaction scripts" com dependências cruzadas entre áreas
- **Onde:** os endpoints são classes estáticas com 8 a 11 dependências injetadas por handler (ex.: `ChoreEndpoints.CompleteChore`, `CalendarEndpoints.UpdateOccurrence`). Regras de negócio vivem na Api e não no Domain: o saldo (`Rewards/Points.cs`), os períodos e valores da mesada (`AllowanceEndpoints.Describe`), quem leva quem, a cor de quem entra. As áreas chamam-se umas às outras através dos endpoints: `MembershipService` → `ChoreEndpoints.ToDto`, `CalendarEndpoints.ToDto`, `CalendarSyncEndpoints.ForgetRemoteAsync`; `ActivityEndpoints` → `ShoppingEndpoints.NotifyCountsAsync`, `RewardEndpoints.ToDto`; `AbsenceEndpoints` → `ChoreEndpoints.NotifyAssignee`. A autorização é manual em cada handler (`RequireAsync` na primeira linha): esquecê-la num endpoint novo expõe dados de outra casa.
- **Impacto na V2:** equipamentos, garantias, casa inteligente e IA em linguagem natural vão precisar de chamar as mesmas operações (criar evento, pôr na lista) fora do HTTP. Hoje isso obriga a duplicar a lógica dos handlers.
- **Correção (incremental):** (1) extrair um serviço de aplicação por área (`ChoreService.CompleteAsync(…)`, `CalendarService`, …) que devolve um resultado com os efeitos, deixando os endpoints a traduzir HTTP; (2) passar o cálculo de saldo e de mesada para o Domain (puro, testável sem base de dados); (3) um `EndpointFilter` de grupo com metadados `(módulo, acesso)` que faz o `RequireAsync` e põe o resultado em `HttpContext.Items`; (4) os DTOs e os `ToDto` num namespace `Contracts` partilhado, em vez de `XEndpoints.ToDto`.

### ENG-17 — O histórico guarda texto português já montado e traduz-se com expressões regulares ao contrário
- **Onde:** `Kubico.Api/Common/Texts.cs:32-75` e `:540-595` (mais de 70 padrões compilados a partir do catálogo, com correspondência por "mais texto fixo"); `ActivityEntry.Summary` (`Kubico.Domain/Activity/ActivityEntry.cs:32`).
- **Problemas:** (a) o texto do utilizador entra no padrão: uma lista chamada «Casa» na lista «X» ou um título com ": " pode corresponder a outro padrão, e a tradução sai trocada ou fica por fazer; (b) uma terceira língua (a especificação fala em pt e en, mas um mercado novo traz outra) obriga a casar padrões de todas as frases; (c) mudar o nome de um membro não muda o histórico (aceitável) e não se pode voltar a montar a frase.
- **Correção:** guardar `SummaryKey` + `SummaryArgs` (JSON) no `ActivityEntry` e nos avisos, e formatar ao ler com o catálogo por chave. Manter o `Summary` atual só para entradas antigas.

### ENG-18 — Os pontos derivam-se das conclusões, sem livro de pontos; `ChoreCompletion` não tem `HouseholdId`
- **Onde:** `Kubico.Api/Rewards/Points.cs:35-44` e `RewardEndpoints.cs:447-483` (a mesma soma escrita duas vezes); `ChoreCompletion` chega à casa pela `Chore` (`c.Chore.HouseholdId`, com um join em todas as consultas de saldo, estatísticas e mesada).
- **Impacto:** é a causa de fundo de ENG-01. Também bloqueia o que vem a seguir no produto: bónus manuais, penalizações, pontos por eventos do calendário ou por rotinas da casa inteligente, transferências entre irmãos, e "zerar" o saldo no início do ano letivo. Cada uma destas coisas exigiria um caso especial nas duas somas.
- **Correção:** tabela `PointsEntry (HouseholdId, UserId, Delta, Reason, SourceType, SourceId, CreatedAt)` escrita por concluir, rejeitar, pedir, aprovar ou recusar, com um índice `(HouseholdId, UserId)`. O saldo passa a ser uma soma sobre uma só tabela. Entretanto, juntar `HouseholdId` a `ChoreCompletion` (com migração que o preenche).

### ENG-19 — Fotos de prova: sem quota e guardadas na base
- **Onde:** `Kubico.Api/Chores/ChoreHistoryEndpoints.cs:48-89`. O `RateLimits` só cobre endpoints anónimos.
- **Cenário:** qualquer membro com `Participate` (uma criança, um convidado) pode enviar fotos de 5 MB sem limite. As não usadas só se apagam ao fim de 1 dia, por isso um script com um token de convidado enche o `bytea` da base (e as cópias de segurança) em minutos. Cada envio faz também um `CleanUpAsync` da casa.
- **Correção:** um limite por pessoa (por exemplo, 20 fotos não usadas, ou um `RateLimiter` particionado por `userId` nos endpoints autenticados de escrita). A médio prazo, guardar as fotos fora da base (blob storage com URL assinado).

---

## Baixo

### ENG-20 — Avisos push: uma falha num aparelho reenvia a todos
`NotificationDispatcher.cs:123-136`: se um envio falha, o *job* inteiro fica por marcar, e na volta seguinte saem outra vez todas as mensagens para todos os aparelhos dessa pessoa, incluindo os que já as receberam. Correção: guardar o resultado por (aviso, aparelho), ou marcar como enviado e repetir só para os aparelhos que falharam.

### ENG-21 — Duplicações que já divergem
- Duas formas de calcular a meia-noite local: `AllowanceEndpoints.StartOfDay` (`:445-449`, `GetUtcOffset` sem tratar horas inválidas) e `ZonedTime.StartOfDay` (`CalendarEvent.cs`, trata-as). Usar só a segunda.
- O saldo calcula-se em dois sítios (ENG-18). `ChoresDoneAsync` (`AllowanceEndpoints.cs:427-442`) repete a consulta de `DescribeAllAsync`.
- `db.Users.Where(u => u.Id == x).Select(u => u.DisplayName)…?? "antigo membro"` aparece pelo menos 6 vezes (Rewards, Allowance, ChoreHistory, Absence). O `Household` é carregado várias vezes no mesmo pedido (ex.: `CalendarEndpoints.NotifyInvolved:527` volta a lê-lo depois de `UpdateEvent:256`).
- `AccountEndpoints.PlanDeletionAsync` usa strings ("Deleted", "Leaves", "Blocked") em vez de um enum. Há também uma consulta por casa (N+1).

### ENG-22 — Estatísticas e histórico das tarefas contam em UTC, não nos dias da casa
`ChoreEndpoints.cs:497-526` e `ChoreHistoryEndpoints.cs:151`: `since = now.AddDays(-period)` é um instante, mas a resposta diz `From = today.AddDays(-period)`. Uma tarefa feita às 23:30 de Lisboa no limite entra ou sai conforme a hora do pedido. Usar `ZonedTime.StartOfDay(today.AddDays(-period), zone)`.

### ENG-23 — `HasOccurrenceOn` percorre a série dia a dia até à data do URL, sem limite
`CalendarEvent.cs` (`HasOccurrenceOn`) e `CalendarEndpoints.cs:470-476`: a `date` de `/occurrences/{date}` não passa pelo `IsSupported`. Um `9999-12-31` numa série diária sem fim percorre cerca de 3 milhões de datas. Correção: validar a data (1900–2200) e usar `rule.Dates(first, notBefore: date)`.

### ENG-24 — `SessionService.ListAsync` lê todos os refresh tokens do utilizador
`SessionService.cs:93-110`: com renovações a cada 15 minutos e tokens guardados até expirarem, são milhares de linhas por aparelho. Agrupar na base (`GroupBy(FamilyId)` com `Min`/`Max`/`Any(active)`).

### ENG-25 — O registo revela que emails têm conta
`AuthEndpoints.cs:77-82` responde 409 "Este email já está registado", e isso anula o cuidado de tempo constante do login (`Dummy`). É um tema de segurança, para o revisor dessa área. Junta-se ao 500 da corrida no registo (ENG-04).

### ENG-26 — `PayRestAsync`: dois commits e contagem líquida
`AllowanceEndpoints.cs:288-309`: o `ExecuteUpdate` grava sozinho e o histórico vai num segundo `SaveChanges` (sem transação). `Math.Max(0, done - paid.ChoresDone)` compara totais: uma tarefa nova mais uma conclusão antiga rejeitada dá 0 em falta. Aceitável para já, mas fica documentado.

### ENG-27 — Pequenas incoerências de permissões
- `TransferAdmin` (`MemberEndpoints.cs:190-197`) não chama `RevokeFeedsWithoutAccessAsync`: um ex-administrador que passa a Adulto sem acesso ao calendário fica com o endereço ICS.
- `GetHousehold` (`HouseholdEndpoints.cs:179`) e `ChildProfileEndpoints` decidem pelo papel (`CanManageContent`) e não pelas permissões por área. Fica coerente com "quem gere os perfis", mas convém deixar isso escrito.
- Uma rotina (com `Steps`) pode ser concluída por `/complete` sem nenhum passo marcado. Se for de propósito (quem gere marca por outra pessoa), restringir a `Manage`.

### ENG-28 — Lote de sincronizações pedidas: uma falha interrompe as restantes
`CalendarSyncSupport.cs:224-246`: o `try` envolve o `foreach` inteiro, e uma exceção não prevista no `SyncAsync` de uma casa (por exemplo, um `DbUpdateException`) salta as outras casas desse lote, que ficam à espera da volta completa (5 min). Mover o `try` para dentro do ciclo.

---

## Top 5 do que mais vale a pena fazer

1. **ENG-05 + ENG-04:** um job de CI com PostgreSQL (migrações e `ConcurrencyTests`) e o tratamento global de violações de unicidade no `ApiExceptionHandler`. É barato e é a rede de segurança para tudo o resto.
2. **ENG-02 + ENG-06:** no desfazer, renovar `UpdatedAt` e validar o que se repõe (fotos, membros, entidade-mãe). Fazer o `Capture` depois do `RemoveAsync`. Hoje o desfazer, uma das funcionalidades "de confiança", deixa os aparelhos dessincronizados e pode dar 500.
3. **ENG-01 + ENG-03:** arquivar em vez de apagar tarefas com conclusões, e reabrir só a última conclusão, mantendo o dia da ocorrência. Os pontos das crianças são o dado mais sensível para a confiança na app.
4. **ENG-07:** paginação por chave no `NotificationDispatcher` (resolve o aviso Skip/Take de Production), e pôr esse aviso a `Throw` nos testes.
5. **ENG-10 + ENG-16 + ENG-18 (preparação da V2):** efeitos depois do commit com outbox ou eventos de domínio, serviços de aplicação por área com autorização num filtro, e um livro de pontos. São a base para equipamentos, casa inteligente e IA sem duplicar a lógica dos endpoints.
