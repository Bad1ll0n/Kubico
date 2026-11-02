# Kubico — Revisão fase 3: engenharia Flutter (app/)

Revisor: engenharia Flutter/mobile. Base: branch `mvp-continuacao`, commit 60bb5a1. Não alterei ficheiros do repositório.

## O que corri

- `flutter analyze`: sem problemas.
- `flutter test`: 125 passam, 2 saltados (`live_api_test.dart`, precisa de `KUBICO_API_URL`), 1 falha: `test/fase1_test.dart:210` (T1). A falha está no teste, não na app (ver APP-13).
- `flutter pub outdated`: as dependências diretas estão quase todas na última versão. Há patches do Firebase (`firebase_core` 5.0.0→5.1.0, `firebase_messaging` 17.0.0→17.0.1) e 18 transitivas presas no `pubspec.lock` (ver APP-15).
- `git status`: os 7 ficheiros `generated_plugin*` modificados só mudam nos fins de linha (LF/CRLF).

Ficaram de fora: design visual e acessibilidade visual (outro revisor), backend.

---

## Crítico

### APP-01 — A fila sem rede das compras deita fora alterações que devia repetir (arranque, 401, 5xx, 429)

`lib/src/shopping/shopping_repository.dart:279-287` e `:355`. Em `_flush`, só `statusCode == 0` (sem rede) conta como "tentar mais tarde". Qualquer outro erro descarta a alteração (`_pending.removeAt(0)`), e só fica um `debugPrint`.

Cenários de perda de dados:
1. **Arranque da app (corrida).** Em `main.dart:25` e `:33`, `session.restore()` e `shopping.restore()` correm em paralelo, sem `await`. `SessionController.restore` faz duas leituras do `flutter_secure_storage` (Keystore no Android, WebCrypto na web; lentas) antes de pôr `api.accessToken`. `ShoppingRepository.restore` lê duas chaves do `SharedPreferencesAsync`, mais rápido, e chama logo `sync()`. O pedido sai sem `Authorization` e recebe 401. `ApiClient._execute:655` não tenta renovar, porque `accessToken == null`. A alteração é descartada. Exemplo: a pessoa juntou artigos no supermercado sem rede, fechou a app e voltou a abri-la em casa: os artigos desaparecem. Isto acontece precisamente no caso de uso que o modo sem rede devia cobrir.
2. **Servidor em baixo ou reinício da API/proxy** (502/503/504), ou `docker compose up` a meio de um envio: tudo o que está na fila perde-se.
3. **429** (limites de pedidos) e **408**: descartados.
4. **401 depois de uma renovação falhada por rede**: o `_renew` devolve false com statusCode 0 e o pedido original lança 401. A alteração é descartada.

**Correção:**
- Classificar os erros. Repetir em 0, 408, 429, 5xx e 401 (este só depois de haver sessão). Descartar só os definitivos (400, 403, 404 que não seja do delete, 409, 422).
- Não sincronizar antes de a sessão estar restaurada. Por exemplo, `shopping.restore()` depois de `await session.restore()`, ou um `SessionController.ready` (Future) por que o `sync()` espera. Também se pode simplesmente não enviar quando `api.accessToken == null`.
- Teste: um `MockClient` que devolve 503 e depois 200 tem de entregar a alteração. Outro: a fila restaurada antes do token não perde nada.

---

## Alto

### APP-02 — Arrancar sem rede manda a pessoa para o ecrã de entrada; um soluço de rede pode apagar a sessão e a fila

`lib/src/session/session_controller.dart:47-66`.
- Sem rede no arranque, `refresh()` (GET /me) falha com 0 e a app chama `_setSignedOut()`. Aparece o `AuthScreen`, com os tokens ainda guardados. Não há nenhuma nova tentativa automática quando a rede volta. A cópia das listas (`ShoppingRepository`) fica inacessível: "offline-first" só funciona se a app já estiver aberta. Se a pessoa entrar de novo, cria uma família de refresh tokens nova, e a antiga fica órfã até expirar.
- Com 5xx no arranque (API a reiniciar), acontece o mesmo.
- **Pior caso:** no arranque o token de acesso quase sempre já expirou (dura 15 min). O GET /me chega ao servidor e recebe 401, depois o POST /auth/refresh falha por rede. `_renew` devolve false e `restore` recebe 401, logo `isUnauthorized`, e chama `signOut()`. Daí segue `_forgetSession()`, depois `onSessionEnded`, e por fim `shopping.clear()`. Resultado: a sessão termina **e a fila por enviar é apagada**, por causa de uma falha de rede.
- Ainda neste caminho: em `_setSignedOut` sem `_forgetSession`, `api.accessToken` continua preenchido e o timer de nova tentativa das compras continua a enviar. Se depois entrar outra conta no mesmo aparelho, a fila e a cópia da conta anterior ficam lá (chaves por `householdId`/`listId`, não por utilizador).

**Correção:**
- Guardar no dispositivo a última resposta de `/api/me` (casas, papéis e `accessTo`, nada de segredos) e entrar em `signedIn` "sem ligação" com ela, voltando a tentar o `refresh()` com backoff e em `AppLifecycleState.resumed`.
- Em `ApiClient`, não converter uma falha de rede na renovação num 401. Propagar o erro de rede (por exemplo `ApiException(0, …)` também no pedido original) e terminar a sessão só quando o próprio `/auth/refresh` responder 401.
- Namespacing da fila e da cópia por `userId`.

### APP-03 — O tempo real morre de vez depois de ~1 minuto sem rede, e não volta a tentar quando a ligação inicial falha

`lib/src/realtime/household_realtime.dart:153-158, 241-257`.
- `withAutomaticReconnect(retryDelays: [0, 2000, 5000, 10000, 30000])` desiste depois de 5 tentativas (~47 s). Não há `onclose`, por isso a ligação fica fechada para sempre. Basta um túnel, o metro, um elevador, o modo de avião ou o telemóvel em segundo plano (o Android e o iOS cortam o socket). Depois disso, a app aberta deixa de receber alterações e avisos (o sino depende do `NotificationCreated`) até se mudar de casa ou reiniciar. Como `reconnected` nunca é emitido, os ecrãs também não recarregam.
- Se `connection.start()` falhar no arranque (sem rede, API a reiniciar), o `catch` só faz `debugPrint`: nunca mais se tenta.
- Ao voltar de segundo plano, nada verifica o estado da ligação.

**Correção:**
- `connection.onclose(...)` com nova ligação em backoff exponencial sem limite (com teto, por exemplo 60 s). A mesma rotina deve tratar a falha do `start()` inicial.
- Um `AppLifecycleListener(onResume: ...)` que, se `state != Connected`, volta a ligar e emite `reconnected`. Também pode juntar `connectivity_plus` para tentar logo que a rede volta.
- Abstrair o `HubConnection` atrás de uma interface para se poder testar (ver APP-13).

### APP-04 — Os pedidos HTTP não têm timeout

`lib/src/api/api_client.dart:646-651`. `_http.send(request)` não tem `.timeout(...)`. Em rede fraca (portal cativo, 1 barra de 3G, Wi-Fi do supermercado que "liga" mas não passa nada), o pedido fica pendurado até ao timeout de TCP do sistema, que pode chegar a minutos.

Consequências:
- `ShoppingRepository.items()` faz `await sync()` antes de ler (linha 162). Uma fila presa deixa a lista num spinner indefinido, sem passar para a cópia local, que é o comportamento sem rede prometido.
- `_syncing` fica ocupado: novas alterações não saem e o `_SyncBanner` mostra "A enviar…" para sempre.
- Os ecrãs de tarefas e calendário ficam em carregamento sem erro nem "Tentar de novo".

**Correção:** um timeout por pedido (por exemplo 15 s; 60 s em uploads e na exportação), convertido em `ApiException(0, …)` para entrar no caminho sem rede. Juntar um teste com um `MockClient` que nunca responde.

---

## Médio

### APP-05 — Mudar de casa depressa pode deixar duas ligações SignalR abertas; vários eventos não dizem de que casa são

- `home_screen.dart:132` chama `realtime.connect(...)` sem `await`. Em `household_realtime.dart:142-147`, duas chamadas seguidas (A→B→C) fazem as duas `await disconnect()` e depois cada uma cria a sua ligação. `_connection` fica com a última e a outra nunca é parada. Continua a emitir para o mesmo `_events`.
- Vários tratamentos não filtram pela casa: `ChoreDeleted` (`chores_view.dart:184`), `ShoppingListDeleted` (`shopping_lists_view.dart:77`), `RewardsChanged` (`rewards_view.dart:76`), `AllowanceChanged` (`allowance_section.dart:47`), `AbsencesChanged` (`household_screen.dart:61`). Com a ligação a mais, o que se passa na casa antiga chega ao ecrã da casa nova: recargas inúteis ou, no caso do `ShoppingListDeleted`, `widget.onDeleted` com um id que não é desta casa.

**Correção:** serializar o `connect`, guardando uma `Future` em curso e um contador de geração (uma ligação de uma geração antiga fecha-se ao terminar o `start`). No backend e no cliente, juntar o `householdId` a todos os eventos e filtrar sempre.

### APP-06 — Alterações recusadas pelo servidor desaparecem em silêncio e o ecrã continua a mostrá-las

`shopping_repository.dart:285` e `shopping_items_view.dart:94-99`. Quando o servidor recusa uma alteração da fila (403 porque o administrador tirou o "Participar" à criança, 404 porque a lista foi apagada, 400 de validação), ela é descartada. A UI mantém o estado otimista: o artigo continua "na lista" até se recarregar. `_onShoppingChanged` só recarrega quando houve um período sem rede (`_waitedOffline`), por isso no caso normal (com rede) a divergência fica até sair do ecrã.

**Correção:** o repositório expõe um stream de "rejeições" (alteração e mensagem). A vista mostra um SnackBar ("Não foi possível pôr «Leite»: …") e reverte o estado ou recarrega a lista.

### APP-07 — "Ignorar versões antigas" compara o relógio do telemóvel com o do servidor; o eco das próprias alterações faz saltar o ecrã

- `ShoppingChange.toItem()` (`shopping_repository.dart:40-51`) põe `updatedAt` com o relógio **do aparelho**. `_upsert` (`shopping_items_view.dart:120-127`) despreza eventos com `updatedAt` mais antigo. Num telemóvel com o relógio 5 min adiantado, durante esses 5 min os eventos do servidor sobre esse artigo (outra pessoa marca-o, o servidor confirma a categoria) são **ignorados**.
- `_toggle` usa `item.copyWith(isChecked: ...)` sem mexer no `updatedAt`. Se se marcar e desmarcar depressa, o eco do primeiro PATCH ("marcado", mais recente que o local) volta a marcar o artigo no ecrã até chegar o eco do segundo: o visto pisca.
- `_load()` (linha 84) substitui `_items` inteiro pela resposta do GET. Os eventos que chegaram enquanto o GET estava em curso perdem-se (o snapshot pode ser anterior a eles).

**Correção:**
- Nos artigos ainda não confirmados, não comparar datas: marcar como "local", e o primeiro evento do servidor ganha sempre.
- Enquanto houver alterações pendentes para um artigo, ignorar os ecos e aplicar `_withPending` por cima do evento, como já se faz ao carregar.
- No `_load`, fundir em vez de substituir, mantendo a versão de `updatedAt` mais recente por id.

### APP-08 — Não há tratamento global de erros nem relatórios de falhas; há futures sem `await` que podem lançar

- `main.dart` não define `FlutterError.onError` nem `PlatformDispatcher.instance.onError`, e não há Crashlytics nem Sentry. Em produção, os erros só aparecem nos logs do aparelho.
- Futures "soltas" que podem falhar:
  - `home_screen.dart:116`: `session.memberChanged(...)`. O `refresh()` lá dentro lança `ApiException` sem rede. Não há `catch`, logo o erro assíncrono fica sem tratamento.
  - `home_screen.dart:132-136`: `connect`, `load`, `register`.
  - `shopping_repository.dart:328`: `_savePending().then(sync)`. Na web, um `QuotaExceededError` do localStorage perde a gravação da fila sem ninguém saber.
- `analysis_options.yaml` só usa `flutter_lints`, sem as regras que apanhariam isto.

**Correção:**
- Ligar `PlatformDispatcher.onError` e `FlutterError.onError` a um serviço de relatórios. Firebase Crashlytics já cabe no projeto `kubico-casa`. Na web e no Windows: Sentry ou um endpoint próprio, sem dados pessoais.
- Ativar `unawaited_futures`, `discarded_futures`, `cancel_subscriptions`, `close_sinks` e `avoid_dynamic_calls` (o `models.dart` faz muitos casts de `dynamic`).

### APP-09 — Arquitetura: os ecrãs falam diretamente com a API, sem estado partilhado por casa; o modo sem rede só existe nas compras

- `ChoresView`, `CalendarView`, `RewardsView`, `ActivityScreen` e `HouseholdScreen` guardam o estado no `State` e chamam o `ApiClient`. Em `home_screen.dart:247-253`, o `switch` destrói o separador anterior, por isso **cada mudança de separador volta a pedir tudo**. As Tarefas fazem 3 GET, o calendário 2. Os dados não sobrevivem entre separadores.
- `getHousehold` (membros, cores) é pedido à parte pelas Tarefas, Calendário, Histórico e Membros, e cada um volta a pedi-lo com `MembersChanged`.
- Tarefas, calendário e recompensas não têm cópia local. Sem rede, mostram só "Sem ligação…". A especificação pede "tempo real e offline-first" como requisito transversal, e as tarefas e rotinas das crianças são o caso mais usado no telemóvel.
- Ficheiros grandes: `models.dart` tem 1732 linhas e `household_screen.dart` 810.

**Correção (incremental):**
- Um `HouseholdStore` por casa (`ChangeNotifier` ou `ValueNotifier`s), com membros, tarefas e calendário do intervalo à vista, alimentado pelo tempo real e com cópia em disco, como no `ShoppingRepository`. As vistas passam a só observar.
- Para não perder o estado dos separadores, um `IndexedStack` com construção preguiçosa.
- Separar `models.dart` por área.

### APP-10 — Android: release assinado com a chave de debug, cópias de segurança automáticas e URL da API por omissão

- `android/app/build.gradle.kts`: o `release` usa `signingConfigs.getByName("debug")`, e não há `minifyEnabled`/R8 configurado. Não serve para a Play Store.
- `AndroidManifest.xml` não define `android:allowBackup` nem `dataExtractionRules`. O auto-backup do Android copia o `shared_preferences` (cópia das listas e fila, com dados da casa) para a conta Google. Também copia o ficheiro cifrado do `flutter_secure_storage`, cuja chave no Keystore não se restaura: noutro aparelho, a leitura falha ou devolve lixo. Isto está documentado pelo próprio pacote; confirmar com a versão 11.
- `config.dart:16-24`: sem `--dart-define=API_URL`, a app de release aponta para `http://10.0.2.2:5281` (cleartext, bloqueado fora do manifest de debug). Uma build de release esquecida fica inutilizável, sem erro claro.

**Correção:**
- Configuração de assinatura via `key.properties` (fora do Git).
- `allowBackup="false"`, ou regras que excluam o `FlutterSecureStorage` e as preferências do Kubico.
- Em `kReleaseMode`, falhar no arranque (ou na build, num passo do CI) se `API_URL` estiver vazio ou não for `https`.

### APP-11 — Avisos no telemóvel: tocar não abre nada, e com a app aberta dependem só do SignalR

- `firebase_push.dart` e `push_registration.dart` não tratam `getInitialMessage` nem `onMessageOpenedApp`: tocar num aviso ("A Ana pediu uma recompensa") abre a app no ecrã de sempre, não na casa nem no item.
- Com a app em primeiro plano, o FCM no Android não mostra notificações. A app conta com o `NotificationCreated` do SignalR, que pode estar morto (APP-03).
- O iOS está por configurar (próximo passo 1, já conhecido): `firebase_options.dart` lança `UnsupportedError` e o `createPushTokenSource` protege.
- `push.unregister()` só corre no "Terminar sessão" deste aparelho (`home_screen.dart:155`). Quando a sessão acaba noutro lado (`onSessionEnded`), o FID fica registado no Firebase. O servidor apaga o `PushDevice`, por isso não há fuga, mas o `_refreshes` continua a ouvir.

**Correção:** tratar o `data` do FCM (`householdId`, área, id) para navegar. Com a app aberta e o SignalR desligado, mostrar em primeiro plano (`flutter_local_notifications`) ou forçar `NotificationCenter.load()`. Chamar `push.unregister()` também no `onSessionEnded`.

### APP-12 — Testes: o teste que falha está errado, e as partes mais frágeis não têm testes

- **T1 (`test/fase1_test.dart:210`):** `DateUtils.dateOnly(DateTime.now()).add(const Duration(days: 1))` dá 23:00 do **mesmo dia** quando o dia tem 25 horas (fim da hora de verão, 25/10/2026). O código da app está certo: `isAvailableOn` e o calendário usam `DateTime(y, m, d + 1)`. Corrigir o teste para `DateTime(t.year, t.month, t.day + 1)`. Também convém fixar o relógio nos testes de widgets, com `withClock` do `package:clock` ou passando o "hoje" por parâmetro, porque vários ecrãs usam `DateTime.now()` diretamente (`chores_view.dart:147,307`, `calendar_view.dart:137,467`, `household_screen.dart:29,453`).
- Não há testes do `HouseholdRealtime`: o `HubConnection` está embutido, sem interface. Nada prova a nova ligação, o `JoinHousehold` depois de religar, nem a troca de casa.
- O modo sem rede só é testado com "sem rede, depois rede" (`shopping_offline_test.dart`). Faltam 5xx, 401 no arranque, timeout e quota.
- O CI (`.github/workflows/ci.yml:36-40`) só constrói a web. Uma quebra no Gradle, num plugin Android/iOS ou no Windows não é apanhada. Juntar pelo menos `flutter build apk --debug` (ubuntu) e `flutter build windows` (windows-latest). O iOS fica para quando houver conta Apple.
- Não há `integration_test` num emulador para o fluxo crítico (entrar, lista sem rede, voltar a ter rede).

### APP-13 — Cada ecrã reage a qualquer evento do tempo real com recargas por conta própria

- `ActivityScreen` (`activity_screen.dart:37-40`): recarrega com **qualquer** evento (incluindo `NotificationReceived` de outra casa e os ecos das próprias alterações). Faz dois pedidos **em série** (`getActivity` e depois `getHousehold`, linha 53) e usa um debounce sem `maxWait`: uma rajada contínua adia a recarga indefinidamente. Devia usar o `CoalescedReload` e `Future.wait`, filtrar pela casa e pedir `getHousehold` só com `MembersChanged`.
- `HomeScreen` e `_Root` fazem `context.watch<SessionController>()`, e qualquer `notifyListeners` (por exemplo `_saveLanguage` depois de cada `refresh`) reconstrói o `Scaffold` inteiro e os separadores. Com as `ValueKey` o estado mantém-se e o custo é baixo, mas `context.select` (casa atual e `accessTo`) evitava-o.

---

## Baixo

### APP-14 — Web: sem rede a app não abre, e os tokens ficam no localStorage

- O service worker do Flutter está descontinuado (o `flutter_bootstrap.js` ainda passa `serviceWorkerSettings`). Sem um service worker próprio que guarde o app shell, recarregar a página sem rede não abre a app: as compras sem rede na web só funcionam com o separador já aberto.
- O `flutter_secure_storage` na web guarda o refresh token (14 dias) cifrado com uma chave que também está no localStorage. Na prática, qualquer XSS lê-o. A CSP do `index.html` mitiga, mas convém registá-lo como risco aceite, ou passar o refresh token para um cookie `HttpOnly; SameSite=Strict` na web.
- O `mobile_scanner` na web carrega o leitor de `cdn.jsdelivr.net` em runtime (dependência externa, autorizada na CSP). Vale a pena servi-lo localmente.

### APP-15 — Dependências

- Diretas: aplicar os patches `firebase_core` 5.1.0 e `firebase_messaging` 17.0.1 (`flutter pub upgrade`). Ao fazê-lo, rever os hashes da CSP se mudar a versão do Firebase JS, como indica o CLAUDE.md.
- `cupertino_icons` não é usado (não há `CupertinoIcons` em `lib/`) e pode sair.
- 18 transitivas presas no lock (`shared_preferences_android` 2.4.28→2.5.3, `objective_c`, `hooks`, …): um `flutter pub upgrade` periódico (Dependabot/Renovate para `pub`).
- Risco: `signalr_netcore` 1.4.4 é mantido pela comunidade, com lançamentos esporádicos. É o pilar do tempo real. Isolá-lo atrás de uma interface (APP-03/APP-12) facilita trocar de pacote ou passar a SSE/WebSocket próprio.
- `file_saver` 0.7.0 ainda está em 0.x (API instável). O `http` não tem timeout por omissão (APP-04).
- Os fins de linha dos `generated_plugin*` mudam a cada `pub get` no Windows: juntar `.gitattributes` (`* text=auto eol=lf`).

### APP-16 — Pormenores

- `shopping_lists_view.dart:197`: o `TextEditingController` do diálogo é libertado em `whenComplete`, antes do fim da animação de saída. Pode dar a asserção "used after being disposed". Usar um `StatefulWidget` no diálogo.
- `voice_input.dart:39`: `_ready ??= initialize(...)`. Se a pessoa recusar o microfone uma vez e depois o autorizar nas definições, o ditado continua indisponível até reiniciar a app. Voltar a tentar quando `_ready == false`.
- `chore_history_screen.dart:69`: a cache estática das fotos (50 fotos, por id) não se limpa ao terminar a sessão. Limpá-la no `onSessionEnded`.
- `shopping_repository.dart:342-343`: um "limpar carrinho" que fica na fila sem rede, quando é enviado, apaga também o que outras pessoas marcaram entretanto. Enviar os ids que estavam marcados no momento.
- `_cacheKeys` só cresce: listas apagadas continuam guardadas, o que pesa na quota de ~5 MB do localStorage na web. Limpar a cópia com `ShoppingListDeleted` e quando `lists()` deixa de as devolver.
- `ApiClient._execute:654-657`: um 401 de um pedido que saiu com o token já substituído volta a renovar e roda o refresh token mais uma vez. Comparar o token usado com o atual antes de chamar `onUnauthorized`.
- "Hoje" nas tarefas e no calendário usa o fuso do aparelho, não o da casa (`Household.Today` no servidor). Numa viagem ou com guarda partilhada noutro fuso, o "disponível hoje" diverge do servidor.
- `calendar_view.dart:115`: `Duration(days: n)` ao converter de dia inteiro para hora marcada atravessa mal a mudança de hora (fica 1 h fora). Usar `DateTime(y, m, d + n, h, min)`.
- Os links de convite só funcionam na web (`main.dart:29`). No Android e no iOS faltam App Links e Universal Links para `…/?convite=`.

---

## Top 5

1. **APP-01:** classificar os erros da fila das compras (repetir 0/408/429/5xx/401) e não sincronizar antes de restaurar a sessão. Hoje o cenário principal do modo sem rede perde artigos.
2. **APP-02:** arrancar sem rede com a última `/me` guardada (sessão "sem ligação") e nunca terminar a sessão por uma falha de rede na renovação.
3. **APP-03:** tempo real que volta sempre (`onclose` com backoff sem limite, nova tentativa na ligação inicial e ao voltar de segundo plano).
4. **APP-04:** timeout em todos os pedidos HTTP, ligado ao caminho sem rede.
5. **APP-08 + APP-12:** relatórios de falhas e lints de futures, um teste do T1 corrigido com o relógio fixo, testes do tempo real (com o `HubConnection` atrás de uma interface) e builds Android e Windows no CI.
