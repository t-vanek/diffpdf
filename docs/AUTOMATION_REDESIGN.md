# Návrh přepisu automatizací — kategorie, účel a uživatelská přívětivost

> **Status:** návrh k odsouhlasení. Tento dokument nemění chování — popisuje cílový stav
> a postup, jak se k němu dostat zpětně kompatibilně.

## 1. Proč to měnit

Dnes je „automatizace" vystavená uživateli jako **syrová pipeline kroků**. V editoru
(`AutomationDefinitionsViewModel`) vybíráš z technického enumu
`AutomationStepType` — `Readiness`, `Health`, `StructureSync`, `Retention`,
`DbRowRetention`, `ScheduledComparison` — a parametry zadáváš jako volný text
`klíč=hodnota`. To má tři problémy:

1. **Není to srozumitelné.** `DbRowRetention` nebo `StructureSync` nikomu neřeknou,
   *k čemu to je* ani *kdy to zapnout*. Účel je schovaný v XML-doc komentářích ve zdrojáku.
2. **Není to kategorizované.** Šest kroků leží v jednom plochém seznamu. Uživatel
   nepozná, co *hlídá* (a má jen upozorňovat), co *uklízí* a co *vykonává práci*.
3. **Parametry jsou neřízené.** `retentionDays`, `maxPerTick`, `enqueueOnly` se píšou
   ručně do textového pole — bez popisků, výchozích hodnot, validace a nápovědy.

Cíl přepisu: **každá automatizace patří do kategorie, má jasný účel vyjádřený lidsky,
a vytváří se z připravené šablony, ne skládáním syrových kroků.**

## 2. Kategorie automatizací

Šest dnešních kroků se přirozeně dělí do tří (volitelně čtyř) kategorií podle toho,
**co automatizace dělá se systémem**:

| Kategorie | Co dělá | Vedlejší efekty | Dnešní kroky |
|---|---|---|---|
| 🔍 **Monitorovací** | Sleduje stav a **upozorní**, když něco není v pořádku. | Žádné — jen čte a notifikuje. | `Health`, `Readiness` |
| ⚙️ **Provozní** (porovnávací) | Vykonává vlastní práci produktu — **spouští porovnání**. | Zakládá joby, generuje reporty. | `ScheduledComparison` |
| 🧹 **Údržbové** | Drží systém zdravý — **uklízí a srovnává strukturu**. | Maže artefakty/řádky, mění scope. | `Retention`, `DbRowRetention`, `StructureSync` |

> **Volitelné jemnější dělení.** `StructureSync` lze vyčlenit do samostatné kategorie
> **🔗 Synchronizační**, pokud chceme „mění strukturu/scope" oddělit od „maže staré
> věci". Doporučuji začít se třemi kategoriemi a čtvrtou přidat, až jich přibude víc
> (např. budoucí import/export, archivace, replikace).

### Mapování krok → kategorie + lidský název + účel

| Krok (`AutomationStepType`) | Kategorie | Lidský název | Účel (jedna věta) |
|---|---|---|---|
| `Health` | Monitorovací | **Zdraví serveru** | Hlídá, že databáze, renderer a úložiště fungují — a upozorní, když ne. |
| `Readiness` | Monitorovací | **Připravenost dat** | Hlídá, že instance v rozsahu mají v `old/` i `new/` čím porovnávat. |
| `ScheduledComparison` | Provozní | **Plánované porovnání** | Pravidelně spustí porovnání pro každou zapnutou instanci v rozsahu. |
| `Retention` | Údržbové | **Úklid reportů** | Maže diff-PDF a JSON reporty dokončených jobů starší než daná lhůta. |
| `DbRowRetention` | Údržbové | **Úklid databáze** | Maže staré řádky (joby, historii) z databáze, aby nerostla bez hranic. |
| `StructureSync` | Údržbové | **Synchronizace struktury** | Srovná složky na disku se scope stromem (větve/instance) v databázi. |

## 3. Co se uživateli změní

### 3.1 Místo „dropdown s krokem" → galerie šablon podle kategorií

Tlačítko **Nová automatizace** otevře **galerii šablon seskupenou do kategorií**.
Každá dlaždice nese ikonu, lidský název, **jednu větu účelu** a doporučenou kadenci/rozsah:

```
🔍 MONITOROVACÍ
   ┌─────────────────────────┐  ┌─────────────────────────┐
   │ ❤️  Zdraví serveru       │  │ ✅  Připravenost dat     │
   │ Hlídá DB, renderer,      │  │ Hlídá, že instance mají  │
   │ úložiště. Upozorní při    │  │ v old/ i new/ co         │
   │ výpadku.                 │  │ porovnávat.              │
   │ Doporučeno: každou 1 min │  │ Doporučeno: každých 5 min│
   └─────────────────────────┘  └─────────────────────────┘

⚙️ PROVOZNÍ
   ┌─────────────────────────┐
   │ 🔁  Plánované porovnání   │
   │ Spustí porovnání pro     │
   │ každou instanci v rozsahu.│
   │ Doporučeno: cron 0 2 * * *│
   └─────────────────────────┘

🧹 ÚDRŽBOVÉ
   ┌─────────────────────────┐  ┌─────────────────────────┐  ┌─────────────────────────┐
   │ 🗂️  Úklid reportů        │  │ 🗄️  Úklid databáze       │  │ 🔗  Synchronizace struktury│
   │ …                        │  │ …                        │  │ …                        │
   └─────────────────────────┘  └─────────────────────────┘  └─────────────────────────┘
```

Po výběru šablony se předvyplní rozumné výchozí hodnoty (kadence, parametry, notifikace).
**Pokročilý režim** ponechává možnost složit vlastní víceкrokovou pipeline jako dnes.

### 3.2 Seznam automatizací seskupený podle kategorie

Sekce **Automatizace** se z plochého seznamu změní na seznam **seskupený do kategorií**
(skládací sekce s ikonou, názvem a počtem + souhrnem stavu OK/Varování/Selhané v záhlaví):

```
🔍 Monitorovací (2)              ● 2 OK
   Zdraví serveru ………………………………… OK   před 30 s
   Připravenost: Alfa ……………………… ⚠ Varování  před 2 min

⚙️ Provozní (1)                  ● 1 OK
   Plánované porovnání ………………………… OK   02:00

🧹 Údržbové (3)                  ● 3 OK
   Úklid reportů …………………………………… OK   03:00
   Úklid databáze ………………………………… OK   03:30
   Synchronizace struktury …………… OK   před 5 min
```

### 3.3 Parametry jako pojmenovaná pole, ne `klíč=hodnota`

Textové pole parametrů nahradí **typovaná pole řízená katalogem** — s popiskem,
nápovědou, výchozí hodnotou a validací. Např. pro **Úklid reportů**:

| Pole | Typ | Výchozí | Nápověda |
|---|---|---|---|
| Doba uchování (dny) | číslo ≥ 0 | 30 | Reporty starší než tolik dní se smažou. |
| Max. mazání na běh | číslo ≥ 1 | 100 | Strop, aby jeden běh nezahltil disk I/O. |

## 4. Změny v doménovém modelu a API

### 4.1 Nový enum kategorií

```csharp
// DiffPdf.Core/Models/AutomationModels.cs
public enum AutomationCategory
{
    Monitoring,    // sleduje a upozorňuje, bez vedlejších efektů
    Operations,    // vykonává vlastní práci (porovnání)
    Maintenance,   // uklízí a srovnává strukturu
    // (volitelně) Synchronization
}
```

### 4.2 Katalog kroků — jediný zdroj pravdy o metadatech

Centrální statický katalog mapuje každý `AutomationStepType` na kategorii, lidský
název, účel, ikonu, doporučenou kadenci a **schéma parametrů** (typovaná pole).
Pohání UI (galerie + seznam + editor), API endpoint i dokumentaci — žádné duplicitní
texty.

```csharp
public sealed record AutomationStepDescriptor(
    AutomationStepType Type,
    AutomationCategory Category,
    string DisplayName,          // "Úklid reportů"
    string Purpose,              // "Maže diff-PDF a JSON reporty starší než lhůta."
    string Icon,                 // glyph / emoji klíč
    string? RecommendedCron,
    int?    RecommendedIntervalSeconds,
    IReadOnlyList<AutomationParameterSpec> Parameters);

public sealed record AutomationParameterSpec(
    string Key,                  // "retentionDays"
    string Label,                // "Doba uchování (dny)"
    string Help,
    AutomationParameterType Type,// Int / Bool / String / Enum
    string? Default,
    int? Min, int? Max);

public static class AutomationCatalog
{
    public static IReadOnlyList<AutomationStepDescriptor> All { get; } = [ /* 6 položek */ ];
    public static AutomationStepDescriptor For(AutomationStepType type) => …;
    public static AutomationCategory CategoryOf(AutomationStepType type) => For(type).Category;
}
```

### 4.3 `Automation` dostane kategorii (odvozenou) + volitelný účel

- **`Category`** — odvozená z „dominantního" kroku (u jednokrokové automatizace je to
  ten krok; u víceкrokové se zvolí podle priority Provozní > Údržbové > Monitorovací,
  nebo se uloží explicitně). Vystavená v `AutomationResponse` jako `category`.
- **`Purpose` / `Description`** — volitelný text, předvyplněný z katalogu, uživatelsky
  editovatelný. Ukáže se v seznamu i v detailu.

Klíče (`Key`) zůstávají stabilní → **žádná migrace dat ani změna REST cest.** Kategorie
je čistě odvozená/aditivní vlastnost.

### 4.4 Nové/rozšířené endpointy

| Endpoint | Účel |
|---|---|
| `GET /api/v1/automations/catalog` | Vrátí katalog: kategorie + kroky s metadaty a schématem parametrů. Pohání galerii a typovaná pole v UI. |
| `GET /api/v1/automations?category=Monitoring` | Filtr seznamu podle kategorie. |
| `AutomationResponse.category` | Kategorie automatizace (odvozená). |
| `AutomationResponse.steps[].displayName / purpose / icon` | Lidská metadata z katalogu (read-only, dopočítaná). |

## 5. Auto-provisioning v kategoriích

`AutomationProvisioner` dnes zakládá baseline automatizace s technickými názvy
(`"Server health"`, `"Report retention"`…). Přepíšeme názvy a doplníme účel
z katalogu — pojmenování bude konzistentní s galerií:

| Klíč | Dnešní název | Nový název | Kategorie |
|---|---|---|---|
| `health` | Server health | **Zdraví serveru** | Monitorovací |
| `readiness-{branch}` | Readiness: {branch} | **Připravenost: {branch}** | Monitorovací |
| `retention` | Report retention | **Úklid reportů** | Údržbové |
| `db-row-retention` | Database row retention | **Úklid databáze** | Údržbové |
| `structure-sync` | Scope structure sync | **Synchronizace struktury** | Údržbové |
| *(žádný baseline)* | — | **Plánované porovnání** (zakládá uživatel) | Provozní |

Provisioning zůstává idempotentní a nedestruktivní (zakládá jen chybějící klíče), takže
přejmenování se projeví jen u nově zakládaných instalací; existující ručně upravené
automatizace se nepřepíšou.

## 6. Postup implementace (zpětně kompatibilní, po vrstvách)

1. **Doména** — přidat `AutomationCategory`, `AutomationCatalog`,
   `AutomationParameterSpec`. Žádná změna `AutomationStepType` (klíče stabilní).
2. **API** — `AutomationResponse` rozšířit o `category` + lidská metadata kroků;
   přidat `GET /automations/catalog` a `?category=` filtr.
3. **Provisioning** — přejmenovat baseline automatizace a navázat účel z katalogu.
4. **Desktop UI** — galerie šablon podle kategorií, seskupený seznam, typovaná pole
   parametrů řízená katalogem; pokročilý režim pro vlastní pipeline ponechat.
5. **Dokumentace** — sekci o automatizacích v `README.md` a `docs/DEVELOPMENT.md`
   přepsat do jazyka kategorií a účelů.

Každý krok je samostatně nasaditelný; uživatelská přívětivost roste postupně a nic
se nerozbije po cestě.

## 7. Shrnutí

- Automatizace dostanou **tři kategorie**: 🔍 Monitorovací, ⚙️ Provozní, 🧹 Údržbové
  (s prostorem pro 🔗 Synchronizační).
- Každý druh automatizace má **lidský název a jednu větu účelu**, vedené z jednoho
  **katalogu**, který pohání UI i API.
- Vytváření jde přes **galerii šablon podle kategorií** s rozumnými výchozími hodnotami;
  parametry jsou **pojmenovaná typovaná pole**, ne volný text.
- Vše **aditivní a zpětně kompatibilní** — klíče a REST cesty se nemění, kategorie je
  odvozená.
