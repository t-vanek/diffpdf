# Analýza a návrh: události, notifikace a automatizace (klient ↔ server)

> **Status:** analýza stavu k 2026-06-12 (větev `main`) + návrh řešení ve třech fázích.
> **Implementováno 2026-06-12** (větev `claude/events-notifications-robustness`): všechny tři
> fáze — N1/N3 opravy, notification outbox s retry a historií doručení, systémový event log
> (`system_events` + `GET /api/v1/events?sinceSeq`) a centrum notifikací v klientu s replayem.
> Vědomé odchylky od návrhu: scope CRUD / změny konfigurace se do event logu nezapisují
> (mají audit log + realtime `branch.*`/`instance.*` push); idempotency key u ručního spuštění
> nebyl potřeba (frontu chrání serverový anti-duplicitní guard a outcome automatizace už UI
> toastuje); version handshake klient–server odložen (tolerantní enum converter řeší pád).
> Cíl: ověřit, že všechny události, notifikace a automatizační kroky si odpovídají mezi
> serverem a desktopovým klientem, najít místa, kde se akce ztrácí nebo nemají adekvátní
> reakci, a navrhnout, jak pipeline vyhladit a zrobustnit.

---

## 1. Shrnutí

Jádro systému (Wolverine pipeline porovnání) je **robustní**: at-least-once doručení,
idempotentní handlery, tři nezávislé recovery vrstvy a leader-gating. Slabá místa jsou
na **okrajích** — tam, kde se má o události dozvědět člověk nebo navazující automatizace:

1. **Event-trigger automatizace nereagují na dávkové události.** UI je nabízí
   („Porovnání dokončeno", „Porušená brána", „Porovnání selhalo"…), ale server je do
   automation sinku nikdy nepošle. Funkce je z pohledu uživatele tiše rozbitá. *(N1)*
2. **E-mailové notifikace jsou best-effort bez perzistence a retry** — selhání SMTP nebo
   chybějící konfigurace znamená tichou ztrátu alertu (jen warning v logu). *(N2)*
3. **Výjimka po terminálním commitu jobu zahodí celou kaskádu** `BatchFinished`/`BatchFailed`
   (notifikace se už nikdy nepošle). *(N3)*
4. **Klient nemá trvalou stopu událostí** — jen pomíjivé toasty a hlavičku stránky; SignalR
   push bez replaye znamená, že kdo nebyl připojen, událost nikdy neuvidí. *(N7)*

Návrh: tři fáze — **(1) vyhladit** existující toky (drobné opravy), **(2) zrobustnit
doručení** (notification outbox s retry a evidencí), **(3) kompletně odchytit akce**
(jednotný append-only event log + centrum notifikací v klientu s replayem po reconnectu).

---

## 2. Inventura: jak dnes toky fungují

### 2.1 Serverová pipeline porovnání (Wolverine, durable local queues)

```
API / trigger / automatizace / fronta větve
        │ RunBatchComparison
        ▼
RunBatchComparisonHandler ──IndexBatch──▶ IndexBatchHandler ──CompareFilePair × N──▶ CompareFilePairHandler
   (Queued → Running,                        │ chyba indexace                            │ poslední pár
    per-branch zámek)                        ▼                                           ▼
                                        BatchFailed ◀─cascade─                      FinalizeBatch
                                             │                                           │
                                             ▼                                           ▼
                            BatchFailedNotificationHandler                    FinalizeBatchHandler
                            BranchQueueAdvanceHandlers                         (report, Complete, cascade)
                                             │                                           │
                                             ▼                                           ▼
                                  NotificationDispatcher ──▶ e-mail        BatchFinished ──▶ BatchFinishedNotificationHandler
                                                                                            BranchQueueAdvanceHandlers
```

- Zprávy: `RunBatchComparison`, `IndexBatch`, `CompareFilePair`, `FinalizeBatch`
  ([FilePairMessages.cs](../src/DiffPdf.Messaging/Messages/FilePairMessages.cs)); doménové
  eventy `BatchFinished`, `BatchFailed` kaskádované jako návratové hodnoty handlerů.
- Retry transientních chyb 3× (2 s, 5 s, 10 s), pak dead-letter
  ([DiffPdfWolverineConfiguration.cs:34](../src/DiffPdf.Messaging/DiffPdfWolverineConfiguration.cs)).
- Idempotence všude: claim guard na párech (`TryClaimAsync`), optimistická verze jobu,
  kontrola statusu ve finalize.

### 2.2 Recovery vrstvy (samoléčení)

| Služba | Interval | Leader | Co řeší |
|---|---|---|---|
| `StaleTaskRecoveryService` | 30 s | ne | páry s propadlou lease (pád workeru uprostřed porovnání) |
| `WorkerLifecycleService` | start/stop | — | graceful shutdown (vrácení vlastních párů) + orphan reclaim po pádu |
| `BranchQueueDispatcherService` | 7 s | ano | zaseknuté neindexované joby, ztracený `FinalizeBatch`, opuštěné Queued páry, posun fronty |
| `JobStalledWatchdogService` | 60 s | ano | alert na job bez postupu > 30 min (jen notifikace, nezasahuje) |
| `AutomationEngineService` | 20 s ±10 % | ano | plánování automatizací (cron/interval), max 4 souběžné běhy |

### 2.3 Realtime push (SignalR, hub `/hubs/jobs`)

| Zpráva | Publikuje | Skupiny | Throttling |
|---|---|---|---|
| `jobProgress` | `SignalRJobProgressPublisher` | `job:`, `instance:`, `branch:`, `scope` | 250/750 ms pro listy; terminální stavy hned |
| `queueState` | `SignalRBranchQueueStatePublisher` | `branch:` | — |
| `triggerEvent` | `SignalRTriggerEventPublisher` | `trigger:`, `job:`, `instance:`, `branch:`, `scope` (jen `branch.*`/`instance.*`) | — |

Pro běhy **automatizací žádný realtime kanál neexistuje** (viz N5).

### 2.4 E-mailové notifikace

10 typů `NotificationEvent` ([NotificationModels.cs:4-38](../src/DiffPdf.Core/Models/NotificationModels.cs)),
pravidla `NotificationSubscription` (příjemci + filtr větev/instance), SMTP nastavení
v DB s fallbackem na `Notifications:Smtp`. Dispatch:
`NotificationDispatcher` → `EmailSender` (MailKit), **best-effort, bez retry a evidence**
([NotificationDispatcher.cs:26-53](../src/DiffPdf.Notifications/NotificationDispatcher.cs)).

### 2.5 Automatizace

Model `Automation` (cron/interval/event triggery, timeout, pokusy, failure threshold),
engine tick 20 s, claim guard, stale-claim window. Event-trigger větev:
`AutomationRunner.NotifyAsync` → `AutomationEventSink` → durable `RunAutomation`
(debounce 60 s, chain depth max 4). Auto-provisioning: Readiness, Health, Retention (cron
3:00), DbRowRetention (cron 4:00), StructureSync.

### 2.6 Klient — jak se dozvídá o změnách

| Mechanismus | Latence | Kde |
|---|---|---|
| SignalR `jobProgress` (koalescence 100 ms, terminální hned) | < 1 s | [JobProgressHubClient.cs:84-96](../src/DiffPdf.DesktopUI/Services/JobProgressHubClient.cs) |
| SignalR `queueState`, `triggerEvent` | < 1 s | Branches/Instances/Jobs VM |
| `PeriodicTimer` 10 s | 10 s | JobsViewModel, BranchesViewModel |
| `DispatcherTimer` 5 s (health/status/readiness) | 5 s | DashboardViewModel |
| Reconnect (`ForeverRetryPolicy` 0/2/5/15 s) → re-join skupin + tichý reload seznamů | — | JobProgressHubClient + VM |
| Ruční obnovení (F5) | — | ViewModelBase |

Reakce uživateli: toasty (info/success 3,5 s, error 7 s), `Error`/`Info` v hlavičce
stránky, potvrzovací dialogy, živý progress bar. Stránka **Automatizace nemá ani hub
odběr, ani auto-refresh** (ověřeno — žádný `PeriodicTimer`/`_hub` v
[AutomationsViewModel.cs](../src/DiffPdf.DesktopUI/ViewModels/AutomationsViewModel.cs)).

---

## 3. Co je dnes robustní (neměnit)

- **At-least-once + idempotence v celé pipeline** — claim guardy, verze, complete-once;
  vícenásobné doručení zprávy je bezpečné.
- **Vrstvené recovery** — pád workeru, ztracený finalize i opuštěné páry mají nezávislé
  záchranné mechanismy s rozumnými lhůtami (lease 12 min, tick 7 s).
- **Vědomé `CancellationToken.None` na terminálních zápisech** (completion, claim
  release) — shutdown neutrhne dokončovací zápis; v kódu zdokumentováno.
- **Leader-gating** přes lease — watchdog, dispatcher a engine neběží duplicitně.
- **Klientský SignalR** — nekonečný reconnect, re-join skupin, koalescence progressu,
  reload po reconnectu; retry v SDK jen pro idempotentní GET (správně).
- **Optimistická konkurence (Version) + ETag cache reportů + per-file upload error kódy.**

---

## 4. Nálezy

Pořadí podle závažnosti. Každý nález má ověřenou evidenci (soubor:řádek).

### N1 — Event-trigger automatizace nereagují na dávkové události *(Kritická — funkční nesoulad UI ↔ server)*

UI nabízí jako spouštěče automatizací všech 10 událostí včetně dávkových: „Porovnání
dokončeno", „Dokončeno s chybami", „Porušená brána", „Porovnání selhalo"
([AutomationDefinitionsViewModel.cs:101-107](../src/DiffPdf.DesktopUI/ViewModels/AutomationDefinitionsViewModel.cs)).
Jenže do `IAutomationEventSink` publikuje **jedině `AutomationRunner`**
([AutomationRunner.cs:28](../src/DiffPdf.Messaging/Automations/AutomationRunner.cs)) — tedy
jen události vzniklé z běhů jiných automatizací (ReadinessFailed, HealthDegraded,
StructureDrift, AutomationFailing, AutomationRecovered).

- `BatchFinishedNotificationHandler` volá **pouze** e-mailový dispatcher
  ([BatchFinishedNotificationHandler.cs:23](../src/DiffPdf.Messaging/Handlers/BatchFinishedNotificationHandler.cs)),
  totéž `BatchFailedNotificationHandler`.
- `JobStalledWatchdogService` rovněž jen `INotificationDispatcher`
  ([JobStalledWatchdogService.cs:75-91](../src/DiffPdf.Messaging/JobStalledWatchdogService.cs)).

**Důsledek:** automatizace „spusť při porušené bráně" (např. ReRunFailed, export, eskalace)
se nikdy nespustí pro reálné porovnání. Uživatel ji nakonfiguruje, uloží, a nic se neděje —
beze stopy.

### N2 — E-mailové notifikace: best-effort bez perzistence, retry a evidence *(Vysoká)*

[NotificationDispatcher.cs:26-53](../src/DiffPdf.Notifications/NotificationDispatcher.cs):

- SMTP nenakonfigurováno → warning do logu a **drop** všech pravidel.
- Selhání odeslání pravidla → warning do logu, **žádný retry, žádný záznam, žádná
  eskalace**. Když selžou všechna pravidla, kritický alert (porušená brána, selhaná dávka,
  zaseknutý job) zmizí beze stopy.
- Neexistuje historie doručení — nelze ani zpětně zjistit, co se (ne)odeslalo.

### N3 — Nechráněné publishe po terminálním commitu → ztráta kaskády *(Vysoká)*

[FinalizeBatchHandler.cs:61-83](../src/DiffPdf.Messaging/Handlers/FinalizeBatchHandler.cs):
po `jobStore.CompleteAsync` (commit) následují `progressPublisher.PublishAsync` a
`triggerEvents.PublishAsync` **bez try/catch**. Když kterýkoli z nich vyhodí výjimku:

1. handler spadne → `BatchFinished` se nevrátí,
2. Wolverine zprávu zopakuje → job už není `Running` → handler vrátí `null`,
3. **kaskáda se už nikdy nespustí**: e-mailová notifikace je trvale ztracená; posun fronty
   zachrání až 7s tick dispatcheru (backstop existuje), notifikace backstop nemá.

Stejný vzor v [IndexBatchHandler.cs:82-92](../src/DiffPdf.Messaging/Handlers/IndexBatchHandler.cs)
(po `FailAsync` publish `comparison.failed` před `return new BatchFailed(...)`).
Pravděpodobnost je nízká (in-proc SignalR), ale dopad je tichá ztráta kritického alertu —
přesně třída chyb, kterou tato analýza hledá.

### N4 — Tiché polykání výjimek v automation řetězci *(Střední)*

- `AutomationEventSink.PublishAsync` — celá metoda v try/catch, jen warning
  ([AutomationEventSink.cs:33-68](../src/DiffPdf.Messaging/Automations/AutomationEventSink.cs)):
  selhání publishe `RunAutomation` = navazující automatizace tiše nevznikne.
- `AutomationRunner.NotifyAsync` — dispatch notifikací (vč. eskalace při překročení
  failure threshold) v try/catch, jen warning
  ([AutomationRunner.cs:185-212](../src/DiffPdf.Messaging/Automations/AutomationRunner.cs)).

Filozofie „notifikace nesmí shodit běh" je správná — chybí ale **evidence a retry**
(řeší Fáze 2), jinak jde o neviditelné výpadky.

### N5 — Automatizace bez realtime kanálu a auto-refreshe *(Střední)*

Server nepublikuje žádný `automation.*` realtime event; stránka Automatizace nemá hub
odběr ani timer. Výsledek běhu (vč. selhání) uživatel uvidí až po ručním F5, nebo
e-mailem (pokud projde přes N2). Monitoring „naživo" tedy reálně nefunguje.

### N6 — Reconnect neobnoví otevřený detail; offline stav není vidět *(Střední)*

- Po `Reconnected` se tiše reloadují jen seznamy
  ([JobsViewModel](../src/DiffPdf.DesktopUI/ViewModels/JobsViewModel.cs) `OnReconnected` →
  `ReloadQuietlyAsync`); otevřený detail jobu zůstane zastaralý, dokud uživatel znovu
  neklikne.
- Auto-refresh smyčky při odpojení tiše přeskakují (`if (!_session.IsConnected) continue;`),
  Dashboard má `toastOnError: false` — **uživatel nemá žádný vizuální signál, že kouká na
  zastaralá data**.

### N7 — Pomíjivost notifikací v UI; SignalR bez replaye *(Střední — hlavní motivace Fáze 3)*

Jediné kanály k uživateli jsou toast (zmizí za 3,5/7 s) a `Error`/`Info` v hlavičce
(přepíše se další akcí). Neexistuje žádná historie událostí v aplikaci. SignalR je
push-only bez sekvence/cursoru: kdo nebyl připojený nebo ve správné skupině, událost
**nikdy** nedostane a nemá jak ji dohledat. Zdroj pravdy je REST, ale uživatel neví, že
se má dívat.

### N8 — Kontraktová křehkost: enumy bez fallbacku, žádný version handshake *(Střední, roste s počtem nasazení)*

[DiffPdfClient.cs:19-23](../src/DiffPdf.Client/DiffPdfClient.cs): `JsonStringEnumConverter`
bez fallbacku — až server přidá novou hodnotu (např. `JobStatus.Retrying`), starší klient
spadne na `JsonException` při deserializaci seznamu jobů. Klient s serverem si při
připojení nevyměňují verzi, takže nekompatibilitu nelze ani detekovat a hlásit česky.
(Enum sady samotné jsou dnes 1:1 — `JobStatus` 7/7 stavů renderováno vč. českých popisků
v [JobRowViewModel.cs:26-70](../src/DiffPdf.DesktopUI/ViewModels/JobRowViewModel.cs).)

### N9 — Drobné mezery v reakcích UI *(Nízká–Střední)*

- Výsledek `RunTriggerAsync` (Success/ErrorCode — `TRIGGER_DISABLED`…) se uživateli
  nezobrazuje; SDK navíc u tohoto volání nevyhazuje výjimku, takže bez explicitního
  zpracování se chyba ztratí úplně.
- API podporuje `Idempotency-Key` pro run trigger, ale UI ho neposílá — rychlý dvojklik
  na „Spustit" může založit dvě dávky.
- `Job.Error` není vidět v řádku seznamu (až po otevření detailu) — selhané joby
  nevysvětlují důvod na první pohled.
- Hromadná queue akce na větvi nevrací per-instance výsledky — částečné selhání je
  všechno-nebo-nic.

### N10 — JobStalled deduplikace jen v paměti leadera *(Nízká)*

Watchdog drží „už alertováno" v in-memory setu — restart nebo změna leadera způsobí
duplicitní alert; výpadek leader lease znamená žádný alert. Přijatelné, ale po zavedení
event logu (Fáze 3) se dedup přesune do DB zadarmo.

### N11 — Testovací mezery kolem notifikací *(Střední)*

- Testy pokrývají jen 3 z 10 `NotificationEvent` typů (Completed/GateViolated, Failed,
  JobStalled); chybí ReadinessFailed, HealthDegraded, StructureDrift,
  AutomationRecovered, CompletedWithErrors, AutomationFailing.
- Žádný test selhání SMTP (dispatcher pokračuje na další pravidlo / drop).
- Žádný test kompatibility enumů (neznámá hodnota ze serveru).
- Žádný test scénáře N3 (výjimka publisheru po commitu).

---

## 5. Návrh řešení

Tři fáze; každá samostatně nasaditelná a zpětně kompatibilní. Fáze 1 a 2 opravují
existující toky, Fáze 3 přidává chybějící páteř („kompletně odchytit akce a udělat
adekvátní reakci").

### Fáze 1 — Vyhladit (rozsah S, jednotky dní)

1. **Napojit dávkové události na automation sink (N1).** V
   `BatchFinishedNotificationHandler`, `BatchFailedNotificationHandler` a
   `JobStalledWatchdogService` po dispatchi e-mailu zavolat i
   `IAutomationEventSink.PublishAsync(event, branchKey, instanceKey, sourceAutomationId: null)`.
   Sink už má debounce, chain-depth i scope matching — jde o čisté dozapojení.
2. **Ochránit post-commit publishe (N3).** V `FinalizeBatchHandler` a `IndexBatchHandler`
   obalit `progressPublisher`/`triggerEvents` try/catch + log. Realtime push je
   best-effort; kaskáda `BatchFinished`/`BatchFailed` je kritická a nesmí na něm záviset.
3. **Reconnect a viditelnost odpojení (N6).** Po `Reconnected` obnovit i otevřený detail
   jobu; do hlavičky okna přidat indikátor stavu spojení („● offline — data mohou být
   zastaralá") řízený z `JobProgressHubClient`.
4. **Adekvátní reakce na ruční spuštění (N9).** UI: generovat `Idempotency-Key` (Guid) na
   kliknutí; zobrazit outcome `RunTrigger` toastem (úspěch i `ErrorCode` česky); přidat
   ikonu/tooltip s `Job.Error` do řádku seznamu jobů.
5. **Tolerantní enum converter v SDK (N8, klientská půlka).** Fallback converter
   (neznámá hodnota → vyhrazený `Unknown` member / bezpečný default + log), ať nové
   serverové stavy neshazují starší klienty.

### Fáze 2 — Zrobustnit doručení notifikací (rozsah M, ~1–2 týdny)

1. **Notification outbox (N2, N4).** Nová tabulka `notification_deliveries`:

   | Sloupec | Význam |
   |---|---|
   | `Id`, `CreatedAt` | identita |
   | `Event`, `BranchKey`, `InstanceKey`, `JobId?`, `AutomationId?` | kontext |
   | `SubscriptionId`, `Recipients`, `Subject`, `Body` | co a komu |
   | `Status` | `Pending` / `Sent` / `Failed` / `DeadLetter` |
   | `AttemptCount`, `NextAttemptAt`, `LastError`, `SentAt` | doručovací stav |

   `NotificationDispatcher` místo přímého odeslání **zapíše řádky** (rychlé, spolehlivé).
   Nový leader-gated `NotificationDeliveryService` (vzor `JobStalledWatchdogService`)
   odesílá s backoffem (1 min → 5 min → 30 min, po 5 pokusech `DeadLetter`).
2. **UI evidence doručení.** V Nastavení → E-mail sekce „Historie doručení" (posledních
   N, filtr na selhání, tlačítko „Odeslat znovu"). Dashboard: badge při `DeadLetter > 0`
   nebo nenakonfigurovaném SMTP při existujících pravidlech.
3. **Stejnou cestou pustit automation notifikace.** `AutomationRunner.NotifyAsync` a sink
   přestanou výjimky polykat „do logu" — zapíší do outboxu (retry zadarmo) a selhání
   zápisu nechají propadnout do Wolverine retry.
4. **Metriky:** `notification_failures_total`, `notification_deadletter_count`,
   `automation_event_publish_failures_total` — podklad pro šablony **QueueHealth** a
   **DeadLetterHealth** z [AUTOMATION_REDESIGN.md](AUTOMATION_REDESIGN.md) (§4).
5. **Testy (N11):** zbývajících 7 event typů, SMTP výpadek (drop → outbox retry),
   dead-letter eskalace, výjimka publisheru po commitu (N3 regres).

### Fáze 3 — Kompletně odchytit akce: systémový event log + centrum notifikací (rozsah L, ~2–4 týdny)

Jediný trvalý, dotazovatelný záznam všeho, co se v systému stalo — server je zdroj
pravdy, klient konzument s replayem.

1. **Server: append-only `system_events`.**
   `Seq` (BIGINT identity — kurzor), `OccurredAt`, `Type` (`job.completed`,
   `job.failed`, `job.recovered`, `automation.run.finished`, `notification.deadletter`,
   `recovery.stale-pairs`, `scope.branch.created`, `config.updated`, `queue.action`…),
   `Severity` (Info/Warning/Error), `BranchKey?`, `InstanceKey?`, `JobId?`,
   `AutomationId?`, `Message` (česky), `Payload` (JSON). Zápis přes `ISystemEventLog`
   volaný z míst, kde už dnes eventy vznikají (finalize, fail, watchdog, recovery služby,
   automation runner, notification delivery, scope/config endpointy) — málo invazivní.
2. **API:** `GET /api/v1/events?sinceSeq=&severity=&limit=` (kurzorové stránkování) +
   SignalR `systemEvent` do `scope` skupiny.
3. **Klient: Centrum notifikací.** Zvoneček s badgem v hlavičce, panel s historií
   (filtr závažnosti/oblasti), perzistovaný `lastSeenSeq`. **Po reconnectu klient dotáhne
   `?sinceSeq=lastSeen` — replay uzavírá hlavní slabinu push modelu** (zmeškané události
   se doženou, nic se neztrácí). Toast zůstává okamžitou reakcí; každý toast má trvalý
   záznam v centru.
4. **Stránka Automatizace naživo (N5):** konzumuje `automation.run.*` eventy → výsledky
   běhů bez F5; `JobStalled` dedup watchdogu se přesune na „existuje event pro tento
   stall?" (řeší N10).
5. **Retence:** `system_events` přibrat do `DbRowRetention` automatizace (default 90 dní).
6. **Volitelné rozšíření:** webhook kanál jako druhý `INotificationChannel` vedle e-mailu
   (navazuje na šablonu **ResultExport** z AUTOMATION_REDESIGN).

### Vazba na AUTOMATION_REDESIGN.md

Návrh je komplementární: redesign řeší *uchopitelnost* automatizací (kategorie, šablony),
tato analýza řeší *spolehlivost signálů*, na kterých automatizace a notifikace stojí.
Fáze 1/N1 je předpokladem, aby šablony typu „CI brána" nebo „Přegenerování chyb" (P2)
vůbec mohly fungovat jako event-triggered; metriky z Fáze 2 jsou datovým podkladem šablon
QueueHealth/DeadLetterHealth.

---

## 6. Prioritizace

| # | Nález | Závažnost | Řeší | Rozsah | Doporučené pořadí |
|---|---|---|---|---|---|
| N1 | Dávkové eventy nespouští automatizace | Kritická | Fáze 1.1 | S | 1 |
| N3 | Ztráta kaskády po commitu | Vysoká | Fáze 1.2 | S | 2 |
| N2 | E-maily bez retry/evidence | Vysoká | Fáze 2.1–2.2 | M | 3 |
| N4 | Polykané výjimky v automation řetězci | Střední | Fáze 2.3 | S | 4 |
| N6 | Reconnect/offline viditelnost | Střední | Fáze 1.3 | S | 5 |
| N9 | Reakce UI (idempotency, outcome, error) | Nízká–Stř. | Fáze 1.4 | S | 6 |
| N8 | Enum fallback + verze | Střední | Fáze 1.5 (+ handshake ve F3) | S | 7 |
| N11 | Testovací mezery | Střední | Fáze 2.5 | M | 8 |
| N7 | Žádná historie událostí / replay | Střední | Fáze 3 | L | 9 |
| N5 | Automatizace bez realtime | Střední | Fáze 3.4 | (součást F3) | 9 |
| N10 | JobStalled dedup v paměti | Nízká | Fáze 3.4 | (součást F3) | 9 |

**Doporučený start:** Fáze 1 celá (vše S, žádné schéma změny, okamžitý efekt), poté
Fáze 2 (outbox je největší skok ve spolehlivosti za nejmenší riziko), Fáze 3 jako
samostatný feature-projekt „Centrum notifikací".
