# Návrh — workflow testování PDF: triáž, schvalování a oprava

> **Status:** návrh k odsouhlasení. Cíl: **odebrat lidem práci, ne jen ji zrychlit.**
> Aditivní vrstva nad stávajícím enginem — klíče, REST cesty i pipeline zůstávají stabilní.

## 1. Kde dnes lidem vzniká práce

Engine dnes končí tím, že řekne **„liší se"** a vygeneruje diff-PDF. Všechno za tím je
ruční práce tří rolí:

| Role | Co dnes dělá ručně | Proč je to zbytečné |
|---|---|---|
| **Tester** | Otevře Úlohy, projde každou `Differs` dvojici, rozhodne „očekávaná změna vs. vada", výsledek někomu pošle mailem. Stejný rozdíl (např. nové logo na 300 fakturách) posuzuje znovu a znovu. Při schválení nové verze ručně kopíruje `new/` → `old/`. | Rozhodnutí se nikam neukládá, systém se z něj nic nenaučí a neumí ho zopakovat. |
| **Analytik** | Skládá si přehled „co se změnilo a proč" z mailů a screenshotů; reportuje stav ručně. | Data existují v DB, ale není souhrn ani export. |
| **Tiskař** (opravuje PDF) | Dostane mail „něco se liší", musí si nechat poslat diff-PDF nebo instalovat desktop GUI; po opravě neví, jak si ji sám ověřit — čeká na další noční běh a na testera. | Nemá vlastní jednoduchý vstup do systému ani smyčku „oprav → ověř". |

Klíčové pozorování: **verdikt enginu není konec procesu.** Konec procesu je lidské
rozhodnutí (schváleno / k opravě) a u vad ověřená oprava. Dokud tohle systém nenese,
zůstává práce na lidech a v mailech.

## 2. Princip návrhu

**Rozhodnutí o rozdílu je first-class resource.** Každá lišící se dvojice projde stavem:

```
                       ┌──────────────── AutoApproved (stejný otisk jako dříve schválený)
   Differs ──▶ Pending ┤
                       ├──▶ Approved  („očekávaná změna")  ──▶ volitelně: povýšit referenci
                       └──▶ Rejected  („vada → k opravě")  ──▶ tiskař opraví ──▶ re-check
                                                                  │ Identical/Approved
                                                                  ▼
                                                               Verified
```

Z toho plyne pět pilířů (kapitoly 3–7). Každý sám o sobě ubírá práci; dohromady udělají
z testování PDF proces, kde **člověk vidí jen to, co opravdu vyžaduje jeho úsudek.**

## 3. Pilíř A — Triáž: schválit / vrátit k opravě (tester)

- Nová entita **`PairReview`**: stav (`Pending` / `Approved` / `Rejected` / `AutoApproved`
  / `Verified`), komentář, kdo, kdy, otisk rozdílu (viz pilíř B).
- **Job-level review stav**: dávka je „vyřízená", až má každá `Differs` dvojice rozhodnutí.
  Dashboard dostane dlaždice **K posouzení · K opravě · Čeká na ověření** — tester ráno
  vidí frontu, ne seznam jobů.
- Desktop **Úlohy → detail**: sloupec *Rozhodnutí*, tlačítka **Schválit** / **Vrátit
  k opravě** (s komentářem), **hromadné schválení** přes filtr, filtr „jen k posouzení".
- API:
  - `PUT /api/v1/jobs/{id}/pairs/{pairId}/review` — `{ state, note }`
  - `GET /api/v1/jobs/{id}/review` — souhrn rozhodnutí dávky
  - `GET /api/v1/reviews?state=Pending&branch=…` — fronta napříč joby

## 4. Pilíř B — Otisk rozdílu: stejnou věc neposuzuj dvakrát

Největší zloděj času testera je **opakovaný stejný rozdíl** — změna šablony se projeví na
stovkách souborů, nebo se týž rozdíl objeví v dalším nočním běhu, protože oprava ještě
nedorazila.

- Engine při porovnání spočítá **`DiffFingerprint`** — normalizovaný hash struktury
  rozdílu (zasažené stránky relativně, geometrie změněných regionů zaokrouhlená na hrubou
  mřížku, množina změněných slov bez dynamických hodnot). Uloží se do reportu i `PairReview`.
- **Auto-triáž:** dvojice, jejíž otisk se shoduje s dříve **schváleným** rozhodnutím na
  téže instanci, dostane rovnou `AutoApproved` (s odkazem na původní rozhodnutí). Dříve
  **zamítnutý** otisk se zvýrazní jako „známá vada, oprava nedorazila" — bez nového
  posuzování, jen se prodlouží existující úkol tiskaře.
- **Hromadná triáž podle otisku:** „Schválit tento rozdíl všude" — jedno kliknutí vyřídí
  všech 300 faktur se stejnou změnou.
- Bezpečnost: auto-approve je **per instance**, dá se vypnout v možnostech instance,
  a v reportu je vždy vidět, *proč* byl pár schválen automaticky.

## 5. Pilíř C — Povýšení reference (baseline promotion)

Dnes po schválení nové verze někdo ručně kopíruje soubory z `new/` do `old/`. To je přesně
ta mechanická práce, kterou má dělat server:

- **`POST /api/v1/jobs/{id}/promote`** — pro schválené dvojice (nebo celou dávku)
  přesune `new/` → `old/`; stará reference se **archivuje** do
  `reports/{jobId}/baseline-archive/` (= undo i audit). V desktopu tlačítko
  **„Schválit dávku a povýšit referenci"**.
- Per-pair varianta pro částečné schválení (`POST …/pairs/{pairId}/promote`).
- **Konstrukční poznámka:** dnes platí invariant „server do `old/`/`new/` jen čte".
  Povýšení bude **jediná řízená výjimka** — explicitní, auditovaná (kdo, kdy, který job),
  vypnutelná per instance (`allowPromotion: false`) pro provozy, kde referenci plní
  externí systém.

## 6. Pilíř D — Učení ignorací z rozhodnutí

Falešné poplachy (datum v patičce, číslo stránky, čárový kód s časem) dnes řeší ručně
psané `ignoreRegions`/`ignoreTextPatterns` v JSON. Místo toho:

- Při triáži přibude třetí akce: **„Ignorovat příště"** — z vybraného zvýrazněného
  regionu (klik do náhledu diff-PDF v desktopu) nebo změněného slova vygeneruje
  `ignoreRegion` / `ignoreTextPattern` do options instance, s popiskem a odkazem na
  rozhodnutí, ze kterého vznikl.
- Pravidla zůstávají normální options — jdou zobrazit, upravit i smazat ve stávajícím
  editoru. Systém se tak **učí z každého posouzení** a šum klesá běh od běhu.

## 7. Pilíř E — Webové review pro tiskaře (bez instalace, bez účtu)

Tiskař nesmí potřebovat desktop GUI, účet ani znalost větví a instancí. Dostane **odkaz
v mailu** a na něm všechno:

- Lehké **read-only HTML stránky přímo na API** (žádný nový deploy):
  - `GET /review/pairs/{pairId}?token=…` — verdikt, komentář testera, **inline náhled
    diff-PDF** (staré vlevo / nové vpravo), tlačítko stáhnout.
  - `GET /review/instances/{key}/fixqueue?token=…` — **fronta oprav**: seznam `Rejected`
    dvojic instance se stavy *Otevřeno → Opraveno → Ověřeno*. Tiskařova jediná stránka:
    „co mám opravit a co už je zelené".
- **Smyčka oprav bez čekání na noc:**
  1. Tiskař opraví PDF a na stránce ho **nahraje** (`POST /review/pairs/{pairId}/upload`,
     soubor jde do `new/` pod původním názvem — případně do staging složky, viz otázky).
  2. Stránka spustí **re-check jen té dvojice** (`POST /api/v1/jobs/{id}/pairs/{pairId}/recheck`
     — nový mini-job přes stávající per-pair pipeline, žádná nová infrastruktura).
  3. Do minuty vidí verdikt. Zelená → stav `Verified`, tester už nic nedělá; jen
    dostane/uvidí potvrzení. Červená → opravuje dál. **Tester z téhle smyčky úplně zmizel.**
- **Přístup přes podepsané token-linky** (HMAC, expirace, scope na instanci/dvojici) —
  funguje i při zapnutém OAuth, žádné účty pro tiskárnu. Volitelně vypnutelné
  (`Review:Enabled=false`).

## 8. Notifikace šité na roli

Stávající odběry (`/api/v1/subscriptions`) se rozšíří o eventy procesní vrstvy; každý mail
nese **přímý token-link** na příslušnou stránku:

| Event | Komu | Obsah |
|---|---|---|
| `ReviewRequested` | tester | Dávka doběhla a má N dvojic k posouzení (po auto-triáži). Link na frontu. |
| `FixRequested` | tiskař | Dvojice vrácena k opravě, komentář testera, link na review stránku + frontu oprav. |
| `FixVerified` | tester / analytik | Oprava nahrána a ověřena (zelená), bez nutnosti cokoli dělat. |
| `Digest` | analytik | Denní/týdenní souhrn za větev: změny, vady, doba do opravy. (V katalogu automatizací už navrženo jako P3 — povýšit na P2.) |

Plus **export pro analytika**: `GET /api/v1/branches/{key}/review-summary?format=csv|json`
— rozhodnutí, otisky, časy od nálezu po ověření. Trend na Dashboardu.

## 9. Jak vypadá den potom

- **Tester** ráno otevře Dashboard: „K posouzení: 4" (z 312 lišících se dvojic — zbytek
  vyřídila auto-triáž podle otisku). Dvě schválí, dvě vrátí tiskaři s komentářem,
  u jedné klikne „ignorovat příště" na datum v patičce. Hotovo za deset minut; kopírování
  složek a maily odpadly.
- **Tiskař** má v mailu link na frontu oprav. Opraví šablonu, nahraje PDF, do minuty vidí
  zelenou. Nikoho neprosí o ověření.
- **Analytik** dostává digest a má CSV export; nic nesbírá ručně.

## 10. Doménový model a API (skica)

```csharp
public sealed record PairReview(
    Guid JobId, Guid PairId,
    ReviewState State,            // Pending / Approved / Rejected / AutoApproved / Verified
    string? Note, string? Reviewer,
    string DiffFingerprint,       // normalizovaný hash rozdílu
    Guid?  AutoApprovedFromId,    // odkaz na původní rozhodnutí
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt);

public enum ReviewState { Pending, Approved, Rejected, AutoApproved, Verified }
```

| Endpoint | Účel |
|---|---|
| `PUT /api/v1/jobs/{id}/pairs/{pairId}/review` | Rozhodnutí (stav + komentář + volitelně `learnIgnore`). |
| `GET /api/v1/jobs/{id}/review` · `GET /api/v1/reviews?state=…` | Souhrn dávky · fronta napříč joby. |
| `POST /api/v1/jobs/{id}/promote` (+ per-pair) | Povýšení reference s archivací. |
| `POST /api/v1/jobs/{id}/pairs/{pairId}/recheck` | Re-check jedné dvojice (mini-job). |
| `GET /review/pairs/{pairId}` · `GET /review/instances/{key}/fixqueue` | HTML pro tiskaře (token-link). |
| `POST /review/pairs/{pairId}/upload` | Nahrání opraveného PDF + spuštění re-checku. |
| `GET /api/v1/branches/{key}/review-summary` | Export pro analytika (CSV/JSON). |

Perzistence: tabulka `PairReviews` (+ index na `DiffFingerprint`), žádná změna stávajících
tabulek. Klient (`DiffPdf.Client`) dostane typované metody; desktop staví na nich.

## 11. Postup implementace (fáze)

**Fáze 0 — rozhodnutí a povýšení (P1):**
1. `PairReview` + `ReviewState` v Core, tabulka + store (SQL i in-memory).
2. Review API (PUT/GET) + job-level review stav.
3. Desktop: triáž v detailu úlohy, dlaždice front na Dashboardu.
4. `promote` (job i pair) s archivací a per-instance vypínačem.

**Fáze 1 — auto-triáž a smyčka oprav (P1/P2):**
5. `DiffFingerprint` v enginu + auto-approve/známá-vada logika + hromadná triáž.
6. Re-check jedné dvojice (mini-job přes stávající pipeline).
7. Web review stránky + token-linky + fronta oprav; eventy `ReviewRequested` / `FixRequested` / `FixVerified`.

**Fáze 2 — méně šumu, víc přehledu (P2):**
8. „Ignorovat příště" — generování ignore pravidel z rozhodnutí (klik do náhledu).
9. Upload opravy z web stránky.
10. `Digest` automatizace + `review-summary` export + trend na Dashboardu.

**Fáze 3 — rozšíření (P3):** přiřazování oprav konkrétním tiskařům, SLA metriky
(doba nález → ověření), komentářová vlákna u dvojice.

Každá fáze je samostatně nasaditelná, aditivní a zpětně kompatibilní; bez fáze 1+ se
systém chová přesně jako dnes.

## 12. Otevřené otázky k rozhodnutí

1. **Kam nahrávat opravu od tiskaře** — přímo do `new/` (jednoduché, ale přepisuje vstup),
   nebo do staging složky `reports/{jobId}/fixes/` s tím, že do `new/` ji přesune až
   úspěšný re-check? (Doporučení: staging.)
2. **Token-linky vs. účty** — stačí HMAC linky s expirací (doporučeno pro start), nebo
   tiskárny potřebují trvalé přihlášení?
3. **Promote = move, nebo copy?** Move šetří místo, copy je bezpečnější; archivace starou
   referenci kryje v obou případech. (Doporučení: move + archiv.)
4. **Granularita auto-approve** — per instance (doporučeno), nebo per větev?

## 12b. Stav implementace

**Hotovo (první krok směrem k pilíři E — cesta k tiskaři):**
- `POST /api/v1/jobs/{id}/send` — odeslání zvýrazněných diff-PDF e-mailem: jedna dvojice,
  výběr (`files`), nebo všechny odlišné dvojice dávky. Početné/velké přílohy se
  automaticky balí do ZIP; limit `Notifications:MaxMailAttachmentMb` (default 20 MB).
  Tělo mailu nese scope, verdikty, metriky po souborech, volitelnou poznámku a deep link
  na úlohu (`Notifications:BaseUrl`).
- `IEmailSender` umí přílohy; výběrová logika je v `DiffMailPlanner` (unit-testovaná).
- SDK: `SendJobDiffsAsync`. Desktop: tlačítko **„✉ Odeslat odlišné"** v detailu úlohy
  (celá dávka) a **„✉ Odeslat"** v okně detailu dvojice (jeden soubor); dialog si
  pamatuje poslední příjemce.

## 13. Shrnutí

- **Rozhodnutí o rozdílu se stává součástí systému** — triáž Approved/Rejected místo mailů.
- **Otisk rozdílu** zařídí, že se nic neposuzuje dvakrát — řádově méně práce testera.
- **Povýšení reference jedním klikem** ruší ruční kopírování složek.
- **Ignorace se učí z posouzení** — šum klesá s každým rozhodnutím.
- **Tiskař dostane token-link, frontu oprav a smyčku oprav s okamžitým ověřením** — bez
  instalace, bez účtu, bez testera uprostřed.
- Vše aditivní, po fázích, bez zásahu do stávajícího enginu a API.
