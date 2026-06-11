# Návrh přepisu automatizací — kategorie, šablony, účel

> **Status:** návrh k odsouhlasení. Velký přepis — rozdělený do vrstev a fází, aby šel
> nasazovat postupně a zpětně kompatibilně (klíče a REST cesty zůstávají stabilní).

## 1. Proč to měnit

Dnes je „automatizace" vystavená uživateli jako **syrová pipeline kroků**. V editoru
vybíráš z technického enumu `AutomationStepType` (`Readiness`, `Health`,
`StructureSync`, `Retention`, `DbRowRetention`, `ScheduledComparison`) a parametry píšeš
jako volný text `klíč=hodnota`. Problémy: není to srozumitelné, není to kategorizované
a parametry jsou neřízené (bez popisků, defaultů, validace).

Cíl: **každá automatizace patří do kategorie, má lidský název a jasný účel, a vytváří se
z editovatelné šablony — ne skládáním syrových kroků.**

## 2. Čtyři kategorie

| Kategorie | Co dělá | Vedlejší efekty |
|---|---|---|
| 🔍 **Monitorovací** | Sleduje stav (server, zdroje, data, fronty) a **upozorní**, když něco není v pořádku. | Žádné — jen čte a notifikuje. |
| ⚙️ **Provozní** | Vykonává vlastní práci produktu — **spouští a řídí porovnání**. | Zakládá joby, generuje reporty. |
| 🧹 **Údržbové** | Drží systém štíhlý — **uklízí artefakty, řádky, dočasné soubory, logy**. | Maže / archivuje data. |
| 🔗 **Synchronizační** | Srovnává a přenáší data **mezi systémem a okolím** (disk ↔ DB, sdílené složky, export). | Mění strukturu/scope, přenáší soubory. |

`StructureSync` se z údržby přesouvá do **Synchronizační** kategorie (srovnává disk ↔ DB).

## 3. Model šablon (editovatelných)

**Šablona = pojmenovaný předpis automatizace** v katalogu: kategorie, lidský název, věta
účelu, ikona, doporučená kadence/rozsah, předvyplněné kroky a parametry.

Klíčové: **šablona je jen výchozí bod.** Když ji vybereš, vznikne **normální automatizace
předvyplněná ze šablony**, kterou pak **libovolně upravíš** běžným CRUD — kadenci,
parametry, rozsah, notifikace, název i účel. Šablona nic „nezamyká"; po vytvoření je to
samostatná automatizace. (Volitelně si lze zapamatovat `TemplateKey`, ze které vznikla,
jen pro informaci v UI.)

```
Galerie šablon  ──vyber──▶  předvyplněná automatizace  ──uprav & ulož──▶  běžící automatizace
   (katalog)                  (editor, vše měnitelné)         (CRUD)
```

Vytváření v UI:
1. **Nová automatizace** → galerie šablon **seskupená do 4 kategorií** (dlaždice: ikona,
   název, věta účelu, doporučená kadence).
2. Výběr šablony → otevře se **editor předvyplněný** hodnotami šablony.
3. Uživatel **cokoli upraví** a uloží. Pokročilý režim navíc umožní složit vlastní
   víceкrokovou pipeline od nuly.

## 4. Katalog automatizací

Legenda: **✅ existuje dnes** · **🆕 nová (navrhovaná)** · priorita **P1** (jádro) /
**P2** (vysoká hodnota) / **P3** (rozšíření).

### 🔍 Monitorovací

| Šablona | Účel | Stav | Pri |
|---|---|---|---|
| **Zdraví serveru** | Hlídá DB, renderer a zápis do úložiště; upozorní při výpadku. | ✅ `Health` | P1 |
| **Připravenost dat** | Hlídá, že instance v rozsahu mají v `old/` i `new/` co porovnávat. | ✅ `Readiness` | P1 |
| **Diskové úložiště** | Hlídá volné místo na svazku artefaktů/DB; upozorní pod prahem (např. < 10 %). | 🆕 `SystemResource(disk)` | P1 |
| **Vytížení CPU** | Hlídá trvale vysoké vytížení CPU nad prahem po dané okno. | 🆕 `SystemResource(cpu)` | P1 |
| **Paměť RAM** | Hlídá dostupnou RAM / working set procesu; upozorní pod prahem. | 🆕 `SystemResource(ram)` | P1 |
| **Fronta a zaseknuté joby** | Hlídá hloubku fronty a joby běžící podezřele dlouho (využije event `JobStalled`). | 🆕 `QueueHealth` | P2 |
| **Čerstvost porovnání** | Upozorní, když instance nevyprodukovala úspěšné porovnání déle než N hodin (tichý výpadek pipeline). | 🆕 `ComparisonFreshness` | P2 |
| **Míra chyb** | Hlídá podíl dvojic končících `Error` přes časové okno; upozorní nad prahem. | 🆕 `ErrorRate` | P3 |
| **Dostupnost úložiště** | Hlídá dosažitelnost a latenci síťových/UNC složek (`share:`). | 🆕 `StorageReachability` | P3 |
| **Stav dead-letter** | Upozorní, když v dead-letter frontě (Wolverine) přibývají zprávy. | 🆕 `DeadLetterHealth` | P3 |

> **Disk / CPU / RAM** stojí na jednom kroku `SystemResource` parametrizovaném polem
> `resource` (`disk` / `cpu` / `ram`) + prahy. Navenek jsou to **tři samostatné šablony**
> s lidskými názvy — přesně ukázka, proč vrstva šablon dává smysl: jeden krok, tři
> srozumitelné automatizace.

### ⚙️ Provozní

| Šablona | Účel | Stav | Pri |
|---|---|---|---|
| **Plánované porovnání** | Pravidelně spustí porovnání pro každou zapnutou instanci v rozsahu. | ✅ `ScheduledComparison` | P1 |
| **CI brána** | Vyhodnotí poslední výsledek proti bráně a vystaví pass/fail pro CI. | 🆕 `GateCheck` | P2 |
| **Přegenerování chyb** | Znovu zařadí dvojice, které skončily `Error` (transientní selhání). | 🆕 `ReRunFailed` | P2 |
| **Souhrnný přehled** | Vytvoří denní/týdenní souhrn změn za větev a pošle notifikaci/digest. | 🆕 `Digest` | P3 |
| **Předehřátí rendereru** | Drží renderer „teplý", aby první porovnání po pauze nebylo pomalé. | 🆕 `RendererWarmup` | P3 |

### 🧹 Údržbové

| Šablona | Účel | Stav | Pri |
|---|---|---|---|
| **Úklid reportů** | Maže diff-PDF a JSON reporty dokončených jobů starší než lhůta. | ✅ `Retention` | P1 |
| **Úklid databáze** | Maže staré řádky (joby, historii), aby DB nerostla bez hranic. | ✅ `DbRowRetention` | P1 |
| **Úklid dočasných souborů** | Maže osiřelé dočasné render-soubory a polovičaté výstupy. | 🆕 `TempCleanup` | P2 |
| **Archivace reportů** | Staré reporty místo smazání zazipuje/přesune do studeného úložiště. | 🆕 `Archive` | P3 |
| **Rotace logů** | Ořeže logy nad rámec Serilog okna a ověří velikost log adresáře. | 🆕 `LogRotation` | P3 |
| **Údržba databáze** | Reorganizace indexů / aktualizace statistik (mimo špičku). | 🆕 `DbMaintenance` | P3 |

### 🔗 Synchronizační

| Šablona | Účel | Stav | Pri |
|---|---|---|---|
| **Synchronizace struktury** | Srovná složky na disku se scope stromem (větve/instance) v DB. | ✅ `StructureSync` | P1 |
| **Synchronizace složek** | Stáhne/zrcadlí `new/` ze zdrojové sdílené složky nebo SFTP. | 🆕 `FolderSync` | P2 |
| **Export výsledků** | Pošle reporty/verdikty do externího systému (webhook, S3, sdílená složka). | 🆕 `ResultExport` | P2 |
| **Záloha databáze** | Vytvoří zálohu DB / snapshot scope konfigurace. | 🆕 `Backup` | P3 |
| **Synchronizace konfigurace** | Načte scope konfiguraci ze souboru/gitu a srovná ji se serverem. | 🆕 `ConfigSync` | P3 |

## 5. Změny v doménovém modelu a API

### 5.1 Kategorie

```csharp
public enum AutomationCategory
{
    Monitoring,        // sleduje a upozorňuje, bez vedlejších efektů
    Operations,        // vykonává vlastní práci (porovnání)
    Maintenance,       // uklízí (artefakty, řádky, temp, logy)
    Synchronization,   // srovnává/přenáší mezi systémem a okolím
}
```

### 5.2 Nové kroky (`AutomationStepType`)

Stávajících 6 zůstává (klíče stabilní). Přibývají podle priority:

```
P1: SystemResource              // disk / cpu / ram (param `resource`)
P2: QueueHealth, ComparisonFreshness, GateCheck, ReRunFailed,
    TempCleanup, FolderSync, ResultExport
P3: ErrorRate, StorageReachability, DeadLetterHealth, Digest, RendererWarmup,
    Archive, LogRotation, DbMaintenance, Backup, ConfigSync
```

Každý nový krok = jeden `IAutomationStepExecutor` (stejný vzor jako dnešní). To je
„velký přepis" — ale aditivní: každý executor jde přidat samostatně.

### 5.3 Katalog — jediný zdroj pravdy

Centrální statický katalog mapuje krok i šablonu na metadata (kategorie, lidský název,
účel, ikona, doporučená kadence, **schéma parametrů**). Pohání galerii, seskupený seznam,
typovaná pole v editoru, API endpoint i dokumentaci.

```csharp
public sealed record AutomationTemplate(
    string Key,                       // "disk-space"
    AutomationCategory Category,
    string DisplayName,               // "Diskové úložiště"
    string Purpose,                   // jedna věta
    string Icon,
    string? RecommendedCron,
    int?    RecommendedIntervalSeconds,
    AutomationScopeKind DefaultScope,
    IReadOnlyList<AutomationStep> Steps,          // předvyplněné kroky + parametry
    IReadOnlyList<NotificationEvent> DefaultEvents);

public sealed record AutomationParameterSpec(
    string Key, string Label, string Help,
    AutomationParameterType Type,     // Int / Bool / String / Enum
    string? Default, int? Min, int? Max,
    IReadOnlyList<string>? EnumValues);

public static class AutomationCatalog
{
    public static IReadOnlyList<AutomationTemplate> Templates { get; }
    public static IReadOnlyList<AutomationParameterSpec> ParametersFor(AutomationStepType type);
    public static AutomationCategory CategoryOf(AutomationStepType type);
    public static string DisplayNameFor(AutomationStepType type);
}
```

### 5.4 `Automation` — aditivní pole

- **`Category`** — odvozená z dominantního kroku (jednokroková = ten krok; víceкroková
  podle priority Provozní > Synchronizační > Údržbové > Monitorovací, nebo uložená).
- **`Purpose`** — volitelný text, předvyplněný ze šablony, uživatelsky editovatelný.
- **`TemplateKey`** *(volitelné)* — ze které šablony vznikla (jen informativní).

Vystaveno v `AutomationResponse` jako `category`, `purpose`, `templateKey` + u kroků
dopočítané `displayName` / `icon`.

### 5.5 Endpointy

| Endpoint | Účel |
|---|---|
| `GET /api/v1/automations/templates` | Katalog šablon seskupený do kategorií (pohání galerii). |
| `GET /api/v1/automations/catalog` | Schéma parametrů a metadata kroků (pohání typovaná pole). |
| `POST /api/v1/automations?fromTemplate={key}` | Vytvoří automatizaci předvyplněnou ze šablony (tělo přepíše defaulty). |
| `GET /api/v1/automations?category=Monitoring` | Filtr seznamu podle kategorie. |

## 6. Desktop UI

- **Galerie šablon** seskupená do 4 kategorií (dlaždice s ikonou, názvem, účelem).
- **Seznam automatizací** seskupený do kategorií se souhrnem stavu OK/Varování/Selhané.
- **Typovaná pole parametrů** řízená katalogem místo `klíč=hodnota`.
- **Pokročilý režim** pro vlastní pipeline ponechat.

## 7. Postup implementace (fáze)

**Fáze 0 — základ (P1):**
1. Doména: `AutomationCategory`, `AutomationCatalog`, `AutomationParameterSpec`,
   `AutomationTemplate`. Žádná změna stávajících `AutomationStepType`.
2. `SystemResource` executor + tři šablony (disk/cpu/ram).
3. API: `category`/`purpose` v response, `GET …/templates`, `GET …/catalog`,
   `?fromTemplate=`, `?category=`.
4. Provisioning: přejmenovat baseline na lidské názvy, navázat kategorii a účel.
5. UI: galerie šablon + seskupený seznam + typovaná pole.
6. Dokumentace: přepsat sekci o automatizacích v `README.md` a `docs/DEVELOPMENT.md`.

**Fáze 1 (P2):** `QueueHealth`, `ComparisonFreshness`, `GateCheck`, `ReRunFailed`,
`TempCleanup`, `FolderSync`, `ResultExport` — každý jako samostatný executor + šablona.

**Fáze 2 (P3):** zbylá rozšíření podle poptávky.

Každá fáze je samostatně nasaditelná a nic nerozbije po cestě.

## 7b. Stav implementace

**Hotovo (backend, Fáze 0 / P1):**
- `AutomationCategory` (4 kategorie) + `AutomationCatalog` (kategorie, lidské názvy,
  účely, schéma parametrů, galerie šablon) v `DiffPdf.Core`.
- Krok `SystemResource` + `SystemResourceStepExecutor` — disk / CPU / RAM proti prahům
  (read-only, jen notifikuje), registrovaný v DI.
- API: `AutomationResponse` má `category` + `purpose`; nové `GET /automations/templates`
  a `GET /automations/catalog`; filtr `GET /automations?category=`.
- Provisioning přejmenován na lidské názvy; klient zná nový krok `SystemResource`.

**Hotovo (backend, Fáze 1 / P2 — monitoring):**
- Krok `QueueHealth` + executor — hloubka fronty (čekající joby) a joby běžící podezřele
  dlouho (zaseknutí). Read-only.
- Krok `ComparisonFreshness` + executor — hlídá stáří posledního úspěšného porovnání na
  instanci (tichý výpadek pipeline). Read-only.
- Oba v katalogu (kategorie/název/účel/parametry + šablony) a registrované v DI; klient
  zná oba nové kroky.

**Hotovo (backend, Fáze 1 / P2 — provozní + synchronizační):**
- Krok `ReRunFailed` (Provozní) + executor — pro instance, jejichž poslední dávka
  selhala (job-level `Failed`, volitelně i pair-level chyby), znovu zařadí porovnání.
  Bezpečné proti cyklení: in-flight job je throttle, okno `withinHours`, pair-chyby
  jen na vyžádání. Staví na `IBatchLauncher`/`IScopeConfigurationResolver`.
- Krok `FolderSync` (Synchronizační) + executor — zrcadlí soubory ze zdrojové složky
  (local / UNC / `share:`, placeholdery `{branchKey}`/`{instanceKey}`) do `new/` (nebo
  `old/`) instancí v rozsahu; kopíruje změněné, volitelně `mirror` maže navíc. Staví na
  `INetworkShareResolver`/`INetworkShareConnector` (stejný přístup jako pipeline).
- Oba v katalogu + šablonách, registrované v DI; klient zná nové kroky.

**Hotovo (desktop UI):**
- Klient (`DiffPdf.Client`): `AutomationCategory`/`AutomationParameterType` enumy,
  `Category`+`Purpose` na `AutomationResponse`, modely šablon/katalogu, metody
  `ListAutomationTemplatesAsync` / `GetAutomationCatalogAsync`.
- Sekce Automatizace: **galerie editovatelných šablon seskupená do 4 kategorií**
  (dlaždice s ikonou, názvem, účelem) — výběr předvyplní editor (vše zůstává
  editovatelné, klíč se navrhne unikátní). Seznam má sloupec **Kategorie**; dropdown
  kroků i historie běhů zobrazují **lidské názvy** místo syrového enumu.
- Build i testy zelené (Core 407 / Client 33 / DesktopUI 75, 0 failed).

**Zbývá (volitelné):** typovaná pole parametrů řízená katalogem (zatím `klíč=hodnota`),
seskupení samotného seznamu do kategorií a zbývající P2/P3 kroky (CI brána, export,
archivace, …). Vše aditivní — bez migrace DB.

> Pozn.: v tomto prostředí není .NET SDK, takže backend nešlo lokálně zkompilovat;
> kód je psaný podle stávajících vzorů a switche nad `AutomationStepType` mají default arm.

## 8. Shrnutí

- **4 kategorie:** 🔍 Monitorovací · ⚙️ Provozní · 🧹 Údržbové · 🔗 Synchronizační.
- **Editovatelné šablony** s lidskými názvy — výběr ze galerie předvyplní automatizaci,
  kterou pak libovolně upravíš.
- Monitoring rozšířen o **disk, CPU, RAM** (+ fronty, čerstvost, míra chyb…).
- Navržena **sada nových automatizací** do každé kategorie s prioritami P1–P3.
- Vše **aditivní a zpětně kompatibilní** — klíče i REST cesty stabilní, kategorie odvozená.
