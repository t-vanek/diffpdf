# Audit správy souborů, cest a synchronizace instancí

Datum: 15. 9. 2026

## Shrnutí

Nalezeno **8 dalších chyb reprodukovaných izolovaným diagnostickým programem**, vedle již potvrzeného chybného kořene v instalátoru. Další 3 problémy byly nalezeny statickou kontrolou kódu.

Rozsah: správce souborů v desktopovém klientovi, přenosová fronta, práce s cestami, konfigurace úložišť a synchronizace instancí. Nejde o audit celé aplikace, PDF enginu ani databázové vrstvy.

Na skutečném serveru byly provedeny jen diagnostické dotazy; přenosy, mazání, přejmenování a synchronizace s aplikováním změn se testovaly pouze s lokálními dočasnými daty nebo simulovaným HTTP transportem. Níže je původní analýza; aktuální stav implementace uvádí následující oddíl.

## Stav oprav v repozitáři

Opravy z 15. 9. 2026, zatím bez nasazení na server:

| Nález | Implementace |
|---|---|
| 1 — změna serveru během přesunu | Přenosy zachytí konkrétní klientské připojení. Při změně session se následující operace zastaví s chybou; smazání neodejde na jiný server. Totéž platí pro vícekrokové příkazy a operace po dialogu. |
| 2 — adresářové odkazy | Přímé cesty včetně předků a cílových názvů odmítají existující reparse pointy. Výpis, hledání a kopie odkazy přeskakují; testováno i s cyklem. |
| 3 — změny spravované struktury | Větve, instance a old/new/reports pod nakonfigurovanou cestou ScopeSync nelze přes správce souborů přejmenovat, přesunout nebo smazat. API vrací 403; práce s PDF a uživatelskými podsložkami zůstává povolena. |
| 4 — rozdílná BasePath | Synchronizace hlásí OutOfRoot i u nalezených klíčů a neprovádí provisioning odlišné cesty. |
| 5 — falešně prázdný panel | Navigace ruší filtr; refresh ho zachovává. Prázdný adresář a prázdný výsledek filtru mají odlišné hlášení. |
| 6 — staré položky panelu | Změna backendu nebo session invaliduje seznam, cestu a výběr. Chyba má trvalé zobrazení přímo v panelu. |
| 7 — mizení chyby dávky | Úklid je vázaný na identitu dávky a před smazáním znovu ověřuje stav položek. |
| 8 — kořen disku | Normalizace zachovává kořenový oddělovač; testy zahrnují diskové i UNC kořeny a potomky. |
| A — síťové profily a aliasy | FileManagerService používá společný resolver a connector. Spojení drží po dobu request scope, včetně streamované odpovědi. Fallback dědí profil ScopeSync; explicitní kořen může použít FileManager:CredentialProfile. |
| B — potlačené chyby enumerace | Chyba přeruší synchronizaci a vrátí neúspěšný report; nepovažuje se za prázdný výsledek. Ověřeno dočasným zákazem ListDirectory na testovací složce. |
| C — blokující výpis/hledání/kopie | Lokální výpis, hledání a kopie běží mimo vlákno UI; enumerace kontroluje zrušení mezi položkami. Jednotlivé blokující volání OS na neodpovídající sdílení nelze přerušit samotným CancellationToken. |

Další implementované opravy:

- **Jediný konfigurační základ `DataRoot`**: odvozuje `data` pro ScopeSync a správce souborů a `storage` pro artefakty. Neprázdná hodnota má přednost před staršími kořeny; bez ní zůstává původní konfigurace funkční. Nový instalátor zapisuje pouze tento základ. Konfigurace se čte až při vyhodnocení options, po přidání konfiguračních providerů hostitele.
- **Výslovné UNC/lokální mapování**: resolver používá nakonfigurované dvojice `Root` / `LocalMountPath` i pro prosté UNC cesty. Vybírá nejdelší shodný kořen na hranici adresáře. Ověřena shoda uložené UNC BasePath při synchronizaci i ochrana instancí přístupných lokální cestou.
- **Zákaz úniku z aliasu**: podcesty obsahující `..` nebo absolutní cestu resolver odmítne; platí také pro mapované UNC cesty.
- **Pravdivá diagnostika kořene**: výpis ani diagnostika nevytváří chybějící kořen. Status zvlášť ověřuje možnost číst seznam položek (`readable`); klient zobrazí chybu čtení před chybou zápisu. SDK toleruje chybějící příznak u staršího serveru. Ověřeno skutečným dočasným zákazem ListDirectory na testovací složce.

Ověření: **631 testů jádra + 167 testů desktopu + 66 testů klienta/API = 864 úspěšných testů**, včetně 20 FileEndpoints integračních testů. Prošel také offline smoke test instalátoru. Tato další sada přidala 21 regresních případů. Sestavení desktopu hlásí dvě dřívější doporučení xUnit; při přestavění SDK také existující varování o chybějící XML dokumentaci.

Zbývající práce: přechod konkrétní produkční konfigurace na `DataRoot` po ověření fyzických cest a případná koordinovaná změna identity/cesty instance. Automatická migrace uložených BasePath ani přesun dat nejsou součástí oprav. Ochrana struktury se opírá o vyřešenou cestu ScopeSync; jiné reprezentace musí mít explicitní mapování sdílení. Produkční konfigurace ani data se v rámci oprav neměnily. Postup je v [deploy/README.md](../deploy/README.md) a [NASAZENI.md](NASAZENI.md).

Omezení ochrany odkazů: kontrola odmítá existující odkazy, ale není atomická proti jinému procesu, který současně přepisuje předky cest. Takové záměny musí omezovat oprávnění úložiště; úplná ochrana proti tomuto závodu vyžaduje operace vázané na otevřené systémové handly. Regresní testy ověřují již existující odkazy, nikoli útok souběžnou výměnou adresáře.

P1 = opravit přednostně kvůli riziku zásahu do nesprávných dat. P2 = funkční chyba nebo nespolehlivá diagnostika.

## Stav skutečného serveru

API serveru `d3s-diffpdf:5275` v této relaci potvrdilo:

- `GET /api/v1/files/status`: `resolvedFrom = FileManager:RootPath`, kořen `d:\DiffPdfData\Data\storage`.
- `GET /api/v1/files/`: prázdný seznam.
- `GET /api/v1/scope/root`: kořen `d:\DiffPdfData\Data\data`.
- Přes SMB existuje `\\d3s-diffpdf\DiffPdfData\data\Alfa\IsuLamaEnergyAlfa\{old,new,reports}`.

Pro tento server tedy lze použít prázdné `FileManager:RootPath` a převzít již správně nastavený kořen ScopeSync. Oprava instalátoru z předchozí části práce řeší nové instalace; existující produkční nastavení bylo ponecháno beze změny.

## Reprodukované chyby

### 1. P1: Přesun může mazat na jiném serveru, než ze kterého četl

**Kód:** `src/DiffPdf.DesktopUI/Services/FileBackends.cs:65`, `src/DiffPdf.DesktopUI/ViewModels/TransferQueueViewModel.cs:181`; změna připojení v `MainViewModel.cs`.

**Příčina:** ServerFileBackend získává klienta při každé operaci z proměnlivé ServerSession. TransferRequest uchovává backend, ale ne konkrétní připojení. Odpojení či změna serveru nevyprazdňuje ani neruší přenosovou frontu.

**Reprodukce:** Zařadit přesun server A → lokální cíl. Po přečtení zdroje, během zápisu cíle, vyměnit klienta session za server B. Skutečná fronta a ServerFileBackend se simulovanými HTTP odpověďmi odeslaly:

```text
Server A: GET /api/v1/files/download?path=document.pdf
Server B: DELETE /api/v1/files?path=document.pdf
Stav přenosu: Done
```

**Dopad:** Pokud stejná cesta existuje na B, může být smazán jiný soubor; originál na A zůstává. Také čekající uploady mohou skončit na jiném serveru.

**Řešení:** Navázat dávku a všechny její operace na neměnné připojení s identifikátorem/generací. Při změně session zastavit nové operace a bezpečně dokončit nebo zrušit rozpracované přenosy. Zdroj mazat pouze přes původní připojení. Invalidovat seznamy serverových panelů a uložené cesty členit podle serveru.

**Regresní test:** Přepnutí session během downloadu, uploadu i před mazáním; nový server nesmí obdržet žádný požadavek původní dávky.

### 2. P1: Adresářový odkaz umožní přístup mimo kořen správce souborů

**Kód:** `src/DiffPdf.Core/Storage/VirtualPath.cs:75`, `src/DiffPdf.Application/Files/FileManagerService.cs:426`.

**Příčina:** Kontrola ověřuje pouze textovou podobu cesty. Neověřuje skutečný cíl Windows junction/symbolického odkazu. Výpis, stahování a rekurzivní kopie mohou odkazy následovat.

**Reprodukce:** V dočasném spravovaném kořeni byl vytvořen junction `link` do jiné dočasné složky mimo tento kořen. V cíli byl pouze syntetický `marker.pdf`. Služba vrátila:

```text
List("link"): Ok, marker.pdf
ResolveDownload("link/marker.pdf"): Ok
Přečten obsah marker.pdf ležícího mimo spravovaný kořen.
```

**Dopad:** Při existenci takového odkazu klient překročí deklarovanou hranici úložiště, v rozsahu oprávnění účtu služby. Nejde o tvrzení, že odkaz existuje na produkčním serveru. Související riziko představuje cyklus při rekurzivním kopírování/hledání.

**Řešení:** Definovat politiku odkazů. Nejjednodušší je odmítnout reparse point v každé části cesty a v rekurzi. Pokud jsou odkazy požadované, kontrolovat konečný fyzický cíl vůči skutečnému kořeni a řešit i závod mezi kontrolou a otevřením. Samotné skrytí odkazu ve výpisu nestačí, protože cestu lze zadat přímo.

**Regresní test:** Výpis, download, upload, copy, move, rename a delete přes odkaz mimo kořen; zvlášť cyklický odkaz.

### 3. P1: Přejmenování instance ve správci souborů vytvoří dvě instance

**Kód:** `src/DiffPdf.Application/Files/FileManagerService.cs:239`, `src/DiffPdf.Messaging/ScopeSync/ScopeSyncService.cs:113` a `:158`.

**Příčina:** Přejmenování/move/delete pracují pouze se souborovým systémem, zatímco ScopeSync samostatně registruje nové adresáře a obnovuje chybějící adresáře evidované v DB.

**Reprodukce:** Zaregistrovat `Alfa/Original`, přes správce souborů přejmenovat na `Renamed`, spustit běžnou synchronizaci s AutoRegister a AutoCreateFolders. Výsledek:

```text
Rename: Ok
V DB: Original, Renamed
Původní adresář Original: znovu vytvořen
```

**Dopad:** Uživatel čeká přejmenování jedné instance, ale vznikne nová a původní zůstane s původní identitou a historií. Mazání a přesouvání spravovaných adresářů rovněž obchází kontroly aktivních úloh z aplikační služby instancí.

**Řešení:** Chránit adresáře větví, instancí a povinné old/new/reports před běžným rename/move/delete. Změny instance provádět vyhrazenou službou koordinující DB, disk, aktivní úlohy a synchronizaci. Umožnění běžné práce se soubory uvnitř těchto složek zachovat podle zvolené politiky.

**Regresní test:** Přejmenování/smazání/přesun spravované instance s historií a s aktivní úlohou, následované synchronizací.

### 4. P2: Synchronizace neodhalí nesoulad uložené cesty instance

**Kód:** `src/DiffPdf.Messaging/ScopeSync/ScopeSyncService.cs:130` a `:165`.

**Příčina:** Když na disku existuje stejná dvojice klíčů větev/instance jako v DB, záznam je označen Existing. Pozdější kontrola BasePath je přeskočena přes `seen.Contains(...)`.

**Reprodukce:** Na disku je `novy-koren/Alfa/Instance`, ale DB stále ukazuje na `stary-koren/Alfa/Instance`. Suchý běh synchronizace vrátí `Ok=true`, `Existing`, `OutOfRootCount=0`.

**Dopad:** Správce souborů zobrazuje novou složku, zatímco porovnávání pracuje podle staré BasePath. To je důležité zejména při sjednocování konfigurace nebo migraci dat.

**Řešení:** Porovnat vyřešenou cestu a profil přístupu i u již nalezených instancí. Nesoulad vykázat jako samostatný stav, bez automatického přepisu DB. Připravit explicitní migraci s kontrolou historie a běžících úloh.

**Regresní test:** Stejné klíče, odlišné kořeny; také ekvivalentní lokální/UNC/alias reprezentace podle zvolené normalizace.

### 5. P2: Aktivní filtr způsobuje falešné hlášení „Prázdná složka“

**Kód:** `src/DiffPdf.DesktopUI/ViewModels/FilePanelViewModel.cs:142`, `:263`, `:276`; text prázdného stavu ve `Views/FilePanelView.axaml`.

**Reprodukce:** V rodiči zadat filtr Alfa, otevřít odpovídající složku obsahující Instance. Po navigaci zůstane filtr Alfa. Diagnostika: `LoadedCount=1`, `VisibleCount=0`, `IsEmpty=true`.

**Dopad:** Existující podsložky vypadají jako chybějící; klient nabízí nahrání do údajně prázdné složky. Příznak velmi podobný původnímu hlášení uživatele, ale u aktuální produkční situace byla potvrzena jiná hlavní příčina.

**Řešení:** Při změně adresáře zrušit lokální filtr, případně ho záměrně uchovat a zobrazit „Filtru neodpovídá žádná položka“ s tlačítkem Zrušit filtr. Rozlišit prázdný adresář, prázdný výsledek a chybu načítání.

**Regresní test:** Navigace s filtrem do neprázdné složky; refresh ve stejném adresáři.

### 6. P2: Po chybě přepnutí panelu zůstanou položky předchozího úložiště

**Kód:** `src/DiffPdf.DesktopUI/ViewModels/FilePanelViewModel.cs:113`; `Views/FilePanelView.axaml` nezobrazuje Error panelu, zatímco rodičovský pohled váže vlastní Error správce.

**Reprodukce:** Úspěšně načíst serverový seznam, přepnout na lokální backend, který při načtení vyhodí IOException. Výsledek: `IsServer=false`, `HasLoaded=false`, ale cesta a řádky stále pocházejí ze serveru.

**Dopad:** Hlavička ukazuje jiné úložiště než seznam. Detail chyby je uložen v panelu, ale v panelu chybí trvalé zobrazení; zbývá dočasný toast. Podobná situace nastává při reconnectu, protože inicializovaný FileManagerViewModel při ActivateAsync načtení přeskočí.

**Řešení:** Při změně backendu/session zrušit načítání, vyprázdnit položky, výběr, cestu a stav hledání; deaktivovat operace do úspěšného načtení. Chybu zobrazit přímo v příslušném panelu s možností opakování. Při obyčejném refreshi lze starý obsah ponechat, ale jasně označit jako neaktuální.

**Regresní test:** Neúspěšná změna backendu, změna serveru a refresh po výpadku.

### 7. P2: Starý časovač fronty smaže novou chybu přenosu

**Kód:** `src/DiffPdf.DesktopUI/ViewModels/TransferQueueViewModel.cs:211`.

**Příčina:** ClearWhenCleanAsync ověří úspěch dávky před čekáním 2,5 s, ale po čekání ověří jen `_pumping`. Neověří identitu dávky ani její současný výsledek.

**Reprodukce:** Dokončit úspěšnou dávku; ihned spustit druhou dávku, která rychle selže. Před původním časovačem má fronta `Count=1, State=Failed`; po něm `Count=0, HasItems=false`.

**Dopad:** Chyba zmizí, přestože neúspěšné přenosy mají zůstat viditelné.

**Řešení:** Časovač navázat na identifikátor dávky; při příchodu další dávky ho zrušit. Těsně před vymazáním znovu ověřit identitu a stav všech položek.

**Regresní test:** Úspěšná dávka A → rychle neúspěšná B před vypršením časovače A.

### 8. P2: Kořen disku se změní na cestu relativní k disku

**Kód:** `src/DiffPdf.Core/Storage/VirtualPath.cs:91`.

**Reprodukce:** `TryResolve(@"C:\", "", ...)` vrátí úspěch a cestu `C:` místo `C:\`.

**Dopad:** Odstranění koncového lomítka mění význam cesty ve Windows: C: je relativní k aktuálnímu adresáři na disku C. Výpis tak může mířit jinam než na kořen; u potomků může navíc selhat kontrola prefixu. Aktuální server používá podadresář, takže tento nález nevysvětluje jeho původní problém.

**Řešení:** Při normalizaci zachovat kořen disku, například použít Path.TrimEndingDirectorySeparator; zvlášť otestovat kořen UNC sdílení. Pokud celé disky nejsou podporovány, odmítnout je už při validaci konfigurace.

**Regresní test:** C:\, D:\, UNC share root, běžný podadresář s koncovým oddělovačem i bez něj.

## Další nálezy ze statické kontroly

Tyto tři body nebyly reprodukovány na skutečném nedostupném sdílení ani zatíženém UI.

### A. P2: ScopeSync a správce souborů řeší síťovou cestu rozdílně

ScopeSync používá INetworkShareResolver a INetworkShareConnector s CredentialProfile. FileManagerService přebírá text ScopeSync.RootPath a používá přímo System.IO. Deklarovaně podporované `share:` aliasy a přístup přes profil proto správce souborů nepřebírá.

**Řešení:** Společná služba pro vyřešení kořene a životnost připojení. Ověřit alias i UNC s účtem odlišným od účtu služby.

### B. P2: Nečitelný adresář může být při synchronizaci vydáván za prázdný

SafeGetDirectories zachytí každou výjimku, zapíše log a vrátí prázdné pole. Report pak může mít `Ok=true`; následný DB průchod může chybně interpretovat nepřečtené adresáře jako chybějící.

**Řešení:** Oddělit prázdný výsledek od chyby enumerace. Propagovat dílčí chyby a neprovádět obnovu struktury podle neúplného průchodu. Testovat výpadek a zákaz ListDirectory.

### C. P2: Lokální nebo UNC operace blokují vlákno UI

LocalFileBackend.ListAsync provádí synchronní enumeraci před vrácením Task.FromResult. Totéž platí pro rekurzivní kopii; volající ji spouští přímo z view-modelu. Na pomalém síťovém disku nemůže UI průběžně reagovat, i když metoda má v názvu Async.

**Řešení:** Přesunout blokující souborové operace mimo UI, doplnit kontrolu zrušení mezi položkami a průběžný stav dlouhých operací. Měřit na pomalém nebo odpojeném UNC připojení.

## Doporučené pořadí oprav

1. Navázat přenosy na konkrétní server, chránit spravované adresáře a uzavřít průchod přes odkazy mimo kořen.
2. Zavést společné řešení kořene a diagnostiku nesouladu disk/DB.
3. Zjednodušit konfiguraci na jeden základní adresář. Zachovat podadresáře data a storage; běžné výsledky instancí již patří do jejich reports, storage slouží mimo jiné jednorázovým porovnáním.
4. Migraci dělat s mapováním starých cest a kontroly existujících BasePath. Pouhé přejmenování konfiguračních klíčů nestačí. Instalátor nesmí potichu přepsat vlastní produkční nastavení.
5. Opravit stavy panelu, filtr, časovač fronty a blokující I/O.

Při návrhu jednoho konfiguračního kořene by správce souborů i ScopeSync odvozovaly data automaticky a jednorázová porovnání storage. Chybně nastavený kořen má být viditelný při startu; diagnostický výpis by neměl tiše založit nový adresář a tvářit se, že prázdné úložiště je správné.

## Ověření

- 133 existujících testů: ScopeSyncServiceTests, VirtualPathTests a FileManagerServiceTests — prošly.
- 54 existujících testů: FileManagerViewModelTests a LocalFileBackendTests — prošly.
- Izolovaný diagnostický program reprodukoval všech 8 výše popsaných chyb na aktuálním kódu. U přepnutí serveru použil skutečné klientské třídy se simulovaným HTTP transportem; u diskových operací skutečné dočasné adresáře.
- Sestavení desktopových testů hlásilo existující varování WindowsBase/WebView2 a dvě xUnit doporučení, ale testy prošly.
- Z toho plyne mezera v pokrytí konkrétních okrajových scénářů; úspěch existující sady tyto chyby nevylučuje.
