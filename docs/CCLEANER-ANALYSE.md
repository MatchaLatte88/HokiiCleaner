# CCleaner 6.41 – Funktionsinventar und Vergleich mit Cleanii

Stand: 2026-10-16 · Zweck: Feature-Ideen für Cleanii sammeln und gegen den Ist-Stand abgleichen.

**Rahmen (aus `KONZEPT.md`):** Übernommen werden nur Funktionsideen und Abläufe. Kein Code, keine Assets, keine Regeldatenbank, keine Texte. Cleanii ist in Phase 1 rein lesend, lokal, ohne Netzwerkzugriff und ohne Telemetrie.

**Lesehilfe – Beleggrade**

| Marke | Bedeutung |
|---|---|
| **[B]** | Beobachtet: direkt in den Daten der Datei gesehen (Text-ID, API-Name, Klassenname, Pfad). |
| **[I]** | Inferenz: aus Beobachtungen geschlossen, nicht selbst gesehen. |
| **[U]** | Unbekannt: nicht geklärt. |

---

## 1. Was untersucht wurde und was nicht

**Zielobjekt:** entpackter Installer von CCleaner 6.41 (`D:\Workspace\Cleanii-RE\extracted\ccleaner-6.41`, 21 Dateien im Hauptordner, 59 Sprachdateien in `Lang\`). Nichts wurde ausgeführt.

**Methoden**

1. **REA `inspect_pe_resources`** auf `Lang\lang-1031.dll` (Deutsch): 245 String-Tabellen, vollständig (`examined_entries` 491, Status `complete`). Evidence-ID `ev_1f3ce1550726d0213d207c37c893efae368dea3edbd8d8100168c51eb4252852`, SHA-256 der Datei `766af5fa17a56e2e3771762a811685cea165201a3e98a647e35c1b248c957b92`. Die Tabellen habe ich anhand der von REA belegten Dateibereiche selbst dekodiert (16 Strings je Block, ID = (Block − 1) × 16 + Index). Ergebnis: **2.972 UI-Texte, alle gelesen**.
2. **Eigene String- und Importsuche** (schlichtes Skript, ASCII und UTF-16) in `CCleaner64.exe`, `CCleanerDU.dll`, `CCleanerPerformanceOptimizer.dll`, `…Service.exe`, `CCleanerReactivator.dll/.exe`. Das ist **keine REA-Evidence**, sondern Textfunde.
3. **Cleanii-Seite:** `docs/KONZEPT.md`, `CategoryRules.cs`, `ScanSession.cs` und der bisherige Gesprächsstand.

**Grenzen – bitte mitlesen**

- **Kein Disassembler.** REA hat hier keinen nativen Provider (Ghidra nicht konfiguriert, Hopper/IDA nicht nutzbar). Ich habe nichts dafür installiert. Folglich gibt es **keine Dekompilierung und keine Kontrollflussanalyse**; alles zu „wie funktioniert es intern“ stützt sich auf Strings, Importe und Klassennamen.
- **Nur Deutsch gelesen.** Ein Text in der Liste beweist nicht, dass die Funktion in 6.41 aktiv, kostenlos oder erreichbar ist. Einige Texte sind erkennbar veraltet (z. B. Text-ID 305 „Win95/NT4“, 122 „Windows XP und Vista“).
- **Keine Laufzeitbeobachtung.** Wie sich die Oberfläche tatsächlich verhält, ist nicht gesehen.
- **Texte sind Belege, keine Vorlage.** Ich zitiere nur Funktionsnamen; eigene Formulierungen für Cleanii sind nötig.

**Korrektur einer früheren Annahme:** `CCleanerDU.dll` ist **nicht** die Festplattenanalyse, sondern die „Driver Updater extension“ [B: Versionsinfo, Importe `DiInstallDriverW`, `SRSetRestorePointW`, Klassennamen `DriverUpdater::…`].

---

## 2. Paketstruktur

| Datei | Rolle | Beleg |
|---|---|---|
| `CCleaner64.exe` / `CCleaner.exe` | Hauptprogramm 64/32 Bit. Neue Bildschirme (Health Check, Software Updater, Treiber, Zeitplan) sind ein **eingebettetes HTML/CSS/Skript-UI** mit Textschlüsseln (`API.Language.getText(…)`); die Werkzeug-Dialoge (z. B. Festplatten-Wiper) sind klassische ATL/WTL-Dialoge (`CToolsDriveWiperCtrl`, Namespace `Piriform`). | [B] |
| `CCleanerDU.dll` | Driver Updater: lädt Treiber, installiert per Treiberspeicher, legt vorher einen Wiederherstellungspunkt an, spricht per WinHTTP mit Servern. | [B] |
| `CCleanerPerformanceOptimizer.dll` + `…Service.exe` | Performance-Optimierer (siehe 4.3). Dienst und DLL sprechen über eine Named Pipe mit JSON-Nachrichten. Quelldateien im Dienst: `ProgramDeactivator.cpp`, `ServiceController.cpp`. | [B] |
| `CCleanerReactivator.dll/.exe` | Gegenstück zum Optimierer, Modul `auto_reactivator`, Export `RunProgram`. Beschreibung der DLL: „Performance Optimizer“. | [B] |
| `CCUpdate.exe` | „Emergency updater“. | [B] |
| `CCleanerBugReport.exe`, `CCleanerCrashDump.exe` | Fehlerberichte und Absturzabbilder. | [B] |
| `libwa*.dll`, `wa_3rd_party_host_*.exe` | „MDES SDK V4“ (Versionsinfo). Zweck nicht geklärt; Anbieter-Plattform für Telemetrie/Lizenz/Updates. | [B] Name, [I] Zweck |
| `Lang\lang-*.dll` | Reine Textdateien (59 Sprachen). | [B] |

---

## 3. Funktionsinventar (aus den UI-Texten, Text-IDs in Klammern)

### 3.1 Cleaner (Bereinigung)

- **Ablauf:** Analysieren, dann Bereinigen. Die Analyse nennt „ungefähre Größe“ und stellt klar, dass noch nichts gelöscht wurde (313). Ergebnisdetail wählbar: Zusammenfassung, erweiterter Bericht, Dateiliste (1467–1470), Sortierung nach Suchbaum, Dateigröße, Dateizahl (708–710), „Beinhaltenden Ordner öffnen“ (706), Export als Textdatei (309) [B].
- **Windows-Bereich (43 Texte, ID 3101–3156; Internet Explorer, Explorer, System, Erweitert):** u. a. Papierkorb, temporäre Dateien, Zwischenablage, **Memory Dumps**, **CHKDSK-Fragmente**, Windows-Logs, IIS-Logs, **Prefetch**, **Windows-Fehlerberichte**, **DNS-Cache**, **Font-Cache**, Ereignisprotokolle, **alte Windows-Installation**, DirectX-Shader-Cache (8601), Übermittlungsoptimierung (3175), Treiberinstallations-Logs (3174), Miniaturansichten, Jump-Listen, Explorer-MRU-Listen [B].
- **Browser/Apps:** rund 20 Browser und Dienste als eigene Gruppen (3026–3044: Firefox, Chrome, Edge, Opera, Brave, Vivaldi, Safari, Thunderbird, Spotify, iTunes, WhatsApp u. a.); je Browser Cache, Verlauf, Cookies, Sitzung, Formulardaten, Kennwörter u. a. (3161–3173). Zusätzlich 127 Texte für App-spezifische Bereinigung (ID-Spanne 9202–9346: Teams, Discord, Telegram, OneDrive, TeamViewer, …) [B].
- **Nebenwirkungen pro Option erklärt** (3201–3214, 8602), z. B. „nächster Spielstart dauert länger, Shader werden neu erzeugt“ oder „Freien Speicher sicher löschen erhöht die Reinigungszeit enorm“ [B].
- **Optionen (1401–1478):** sicheres Löschen (1×, 3×, 7×, 35× überschreiben), freien Speicher und freien MFT-Speicher überschreiben, alternative Datenströme und Cluster-Tips löschen, Windows-Temp nur älter als 24 h, Papierkorb nur älter als 24 h, Herunterfahren nach Bereinigung, Einstellungen in INI-Datei, Jump-Lists, Detailgrad, **laufende Programme schließen** (502–510, 714–716: Rückfrage, Wartezeit, Zwangsbeenden) [B].
- **Cookies (1501–1525, 800–805):** Behalten-Liste, intelligenter Scan, Import/Export [B].
- **Einschließen/Ausschließen (1601–1649):** eigene Ordner, Dateien, Dateitypen, Registry-Schlüssel, Mindestalter in Stunden; Wurzel- und Systemordner sind unzulässig (1605, 1606) [B].
- **Benutzer (1701–1710):** aktueller, alle oder ausgewählte Benutzer [B].
- **Regelformat:** ein Kommentar im Programm verweist auf `winapp2.ini` als kompatibles Format für eigene Regeln [B Text, I Bedeutung].

### 3.2 Registry-Cleaner

21 Kategorien (3601–3621) und 23 Fehlertypen (3701–3723): fehlende DLLs, ungenutzte Dateiendungen, ActiveX/COM, Schriftarten, Anwendungspfade, Hilfedateien, Installer-/Uninstaller-Verweise, Autostart-Reste, MUI-Cache, TypeLibs, ungültige Firewallregeln u. a.; Beheben mit vorheriger Registry-Sicherung (172) und „zu Ausnahmen hinzufügen“ (186) [B].

### 3.3 Health Check (früher „Easy Clean“) und Startansicht

- Vier Säulen: **Privatsphäre** (Verlauf, Cookies, temporäre Internetdateien), **Speicher** (Papierkorb, temporäre System- und App-Dateien), **Geschwindigkeit** (Autostart-Apps, Performance-Optimierer), **Sicherheit** (veraltete Software, Treiber) [B: UI-Modelle `HealthCheckPrivacy/Space/…`, Texte 8237–8244, 9966–9969].
- **Gesamtbewertung** „Zustand des PCs“: schlecht / mittel / gut (10072–10081), erklärt als Funktion aus Anzahl und Schwere der Probleme [B].
- Ergebnisseiten mit Drilldown je Säule, „Probleme beseitigen“, Abschlussbericht „Folgendes haben wir erledigt“ (8249–8253) [B].
- Hilfe-Hub, Onboarding, Feedback-Abfrage nach Nutzung [B].
- **Bezahlschranke:** Beheben der Geschwindigkeits-/Sicherheitsprobleme und die erweiterte Bereinigung sind Pro (9771, 9994: „seit kurzem eine kostenpflichtige Funktion“) [B].

### 3.4 Performance-Optimierer

- **Idee:** Programme, die im Hintergrund laufen, werden in den „Schlafmodus“ versetzt; sie „wachen automatisch auf“, wenn man sie benutzt (8953–8962, 9014, 9104) [B]. Geöffnete Programme werden nicht beendet (9137, 9151) [B].
- **Liste:** Programm, **Einfluss auf die PC-Leistung (Niedrig/Mittel/Hoch)**, Typ des Hintergrundeintrags (**Dienst, geplante Aufgabe, Autostart, Registry-Lauf**, 9020–9023), Installationsdatum, aktive vs. schlafende Programme, Ignorieren-Liste (8978–8992, 9112–9116) [B].
- **79 programmspezifische Hinweistexte** (ID-Spanne 9024–9102): jeweils Funktion des Programms, was bei Deaktivierung entfällt, Empfehlung („sicher optimierbar“, „nicht optimieren, wenn Sie rund um die Uhr synchronisieren“) [B].
- **Voraussetzungen:** Administratorrechte und laufender Dienst (8998, 9155–9161) [B].
- **Technik** [B: Importe und Klassennamen]: Dienstsystem (`ChangeServiceConfigW`, `ControlService(STOP)`, `EnumServicesStatusExW`), Aufgabenplanung (`ITaskService`), Registry (`RegSetValueExW`, `RegDeleteValueW`), Prozessliste (`CreateToolhelp32Snapshot`), Klassen `ProgramManager`, `StartupManager`, `ScheduleManager`, `RegistryRunEntry`, `ScheduledTaskEntry`, `ServiceEntry`, **`CTUJournal`** (Journal), lokale SQLite-Datenbank mit Tabelle `IgnoredPrograms (TUID, Time)`, ein „Performance-Impact-Cache“ („Failed to load performance impact cache from DB“), Meldung „Failed to get the current sleeping programs“.
- **[I]** „Schlafmodus“ bedeutet: Autostart-, Dienst- und Aufgaben-Einträge eines Programms werden deaktiviert, in einem Journal festgehalten und beim Start des Programms vom Reaktivierer wiederhergestellt. Das passt zu allen Beobachtungen, ist aber nicht per Disassembly bestätigt.
- **[U]** Woher die Einstufung Niedrig/Mittel/Hoch stammt (Messung, Definitionsdatei oder Server); Bedeutung von „TUID“.

### 3.5 Extras

| Werkzeug | Inhalt | Beleg |
|---|---|---|
| **Deinstallieren** (2011, 2071–2109) | Liste mit Programm, Herausgeber, Installationsdatum, **Größe**, Version; Deinstallieren, Umbenennen, Löschen des Eintrags, Reparieren, Suche. | [B] |
| **Autostart** (2013, 2201–2226, 2250) | Reiter: Windows, **geplante Aufgaben**, **Kontextmenü**, Browser, **Windows-Dienste**; aktivieren, deaktivieren, löschen; Spalten u. a. Schlüssel, Programm, Datei, Produkt, Ort, Profil. | [B] |
| **Browser-Plugins** (2017, 2221–2225) | Erweiterungen von Edge, Chrome, Firefox auflisten, aktivieren, entfernen, Liste speichern (8479). | [B] |
| **Systemwiederherstellung** (2014, 2301–2307) | Wiederherstellungspunkte mit Datum und Beschreibung entfernen; der neueste ist geschützt. | [B] |
| **Festplatten-Wiper** (2015, 2401–2416) | Freien Speicher oder ganzes Laufwerk überschreiben, 1/3/7/35 Durchgänge, Bestätigung durch Eintippen eines Wortes, **Warnung bei SSD** wegen Verschleiß. | [B] |
| **Duplicate Finder** (2016, 2501–2537) | Suchkriterien Name, Größe, Änderungsdatum, **Inhalt**; Ignorieren: 0-Byte, System-, schreibgeschützte, versteckte Dateien; Größenschranken; Ordner einschließen/ausschließen; „Duplikate wählen in“; Ergebnis wiederherstellen; Spalten Name, Pfad, Größe, Erstelldatum. | [B] Funktionsumfang; [U] Algorithmus (Hash?) |
| **Software Updater** (2018, 2601–2762, 9887–9930) | Definitionen werden geladen, installierte Software verglichen, Schweregrade kritisch/wichtig/mittel/niedrig, Release-Notes, „Alle aktualisieren“; Prüfung von **Größe, Prüfsumme und HTTPS** der Update-Dateien (2732, 2733, 2743). | [B] |
| **Driver Updater** (8481–8752, 9582–9732) | Scan veralteter Treiber, Update, **Zurücksetzen auf frühere Version**, Überspringen, Pausieren, Kategorien nach Gerätetyp, **Wiederherstellungspunkt vor jedem Update**, Hinweis auf Beta; braucht Admin und Internet. | [B] |
| **Disk Analyzer** (3932, 3905–3945) | Beschreibung: den Computer durchsuchen, um zu sehen, wie der Festplattenspeicher belegt ist (3942); Startkachel: die Dateien mit dem größten Platzbedarf finden und löschen (9579; der deutsche Text sagt „Treiber“, gemeint sind wohl Laufwerke). Ergebnis als **Dateiliste nach 7 Kategorien** (Bilder, Musik, Dokumente, Video, Komprimiert, E-Mail, Sonstige); Spalten Name, Pfad, Größe, Typ, **Zugriffs-, Änderungs-, Erstellzeit, Attribute**; Suche, „Beinhaltenden Ordner öffnen“, Dateien löschen, Export als Textdatei, Hinweis „Unvollständige Ergebnisse“, Anzeige „Freier / Gesamter Speicher“. | [B] Texte; **[I] keine Ordneransicht, keine Treemap** (kein dazu passender Text gefunden); Ausprägung der Oberfläche [U] |

### 3.6 Einstellungen und Betrieb

Smart Cleaning (Browser automatisch nach dem Schließen bereinigen, Hinweis wenn mehr als X GB bereinigbar, Prüfintervall in Stunden; 1801–1837), **Zeitplan** (einmalig, täglich, wöchentlich, monatlich, beim Systemstart; 1901–1960, 9416–9497), Bereinigungserinnerungen, Updates, Datenschutz-Schalter für Nutzungsdaten (1462–1478), Windows Jump-Liste, Start im Infobereich, Kontextmenü-Einträge im Papierkorb, Kommandozeilen-Scripting (Business-Edition, 994), „Als Administrator neu starten“ (10035) [B].

### 3.7 Nicht-funktionale Beobachtungen

- **Analytics im Produkt:** das UI ruft `API.Analytics.sendEvent(…)` auf; dazu die MDES-Bibliothek [B]. Datenfreigabe-Optionen für „Angebote von Drittanbietern“ (1463) [B].
- **Server:** `csw.client.piriform.com` (und `norton.com`) in `CCleanerDU.dll` [B].
- **Upselling:** Testversion, Rabattaktionen, Pro/Pro Plus/Premium, Installer mit Zusatzangebot (8818), Browser- und Antivirus-Werbung [B].

---

## 4. Vergleich mit Cleanii

Cleanii-Stand laut `KONZEPT.md` und dem aktuellen Code (Orte-Ansicht als Mini-Browser, 145 Tests).

### 4.1 Wo Cleanii bereits weiter oder anders ist

| Thema | CCleaner | Cleanii |
|---|---|---|
| Platzanalyse | Dateiliste nach 7 Kategorien [B/I] | Ordnerbaum, Treemap, Sonar, Zusammensetzung je Ordner nach 14 Typen, Spur zum größten Inhalt, Einzeldateien je Ordner |
| Scan-Genauigkeit | [U] | Belegung auf dem Datenträger, Hardlinks (WinSxS), Reparse Points nicht verfolgt, Cloud-Platzhalter 0 Byte, nicht lesbare Pfade sichtbar |
| Transparenz der Befunde | Gesamtbewertung aus Anzahl und Schwere der Probleme (10081); Messwerte und Schwellen je Befund nicht beobachtet [U] | Befund mit Messwert, Schwelle, Quelle; „nicht gemessen“ mit Grund |
| Datenschutz | Analytics, Drittangebote | lokal, keine Telemetrie, keine Netzwerkzugriffe |
| Geschäftsmodell | Bezahlschranken | keine |

### 4.2 Lücken und Kandidaten, nach Nutzen und Phase-1-Tauglichkeit

**Priorität A – lesend, nutzt vorhandene Daten, schließt bekannte Grenzen**

| # | Idee | Beleg bei CCleaner | Stand Cleanii | Hinweis |
|---|---|---|---|---|
| A1 | **Zeitstempel und Attribute** je Datei/Ordner (Änderung, Erstellung, Zugriff) mit Spalte und Sortierung; Filter „groß und lange nicht geändert“ | Disk Analyzer: Zeit- und Attributspalten [B] | fehlt; `KONZEPT.md` nennt es als Grenze | Der Scanner nutzt `FileSystemEnumerable<…>/FileSystemEntry` (`ScanSession.cs`); dieser Typ bietet die Zeiten direkt aus der Auflistung. **Vor Umsetzung prüfen.** Zugriffszeit ist auf NTFS oft deaktiviert und daher unzuverlässig. |
| A2 | **Export** von Listen (Text/CSV/JSON) | „In Textdatei speichern“ in Cleaner und Disk Analyzer [B] | Meilenstein 5, offen | Im Konzept als erlaubte Phase-1-Funktion vorgesehen. |
| A3 | **Suche** in der aktuellen Ansicht (Name, Endung) | Suchfeld im Disk Analyzer [B] | fehlt | Baum liegt im Speicher, kein Scan nötig. |
| A4 | **Platzgewinn-Potenzial** in der Übersicht: Summe aus Papierkorb, Temp und Caches, Windows.old u. a., nur anzeigen | Health Check „Speicher“ [B] | Daten vorhanden (Typen), keine Zusammenfassung | Rein lesend; ohne Löschknopf. |
| A5 | **Folgenhinweise** pro Kategorie/Befund („Was passiert, wenn das weg ist / ausgeschaltet wird“) | Nebenwirkungstexte 3201–3214 [B]; programmspezifische Hinweise 9024–9102 [B] | Befunde haben Erklärung, Typen nicht | Eigene Texte schreiben, keine fremden übernehmen. |
| A6 | **Lücken in den Kategorie-Regeln** prüfen (siehe 4.3) | Windows-Liste 3101–3156 [B] | teilweise vorhanden | Pfade aus Windows-Wissen, nicht aus CCleaner belegt: am Testrechner verifizieren. |

**Priorität B – neue Messungen, weiterhin lesend**

| # | Idee | Beleg | Anmerkung |
|---|---|---|---|
| B1 | **Duplikat-Bericht** (nur melden): Größe, dann Teil-Hash, dann Voll-Hash; Ausschlüsse (0 Byte, System, versteckt), Mindestgröße, gewählter Ordner, Abbruch | Kriterien und Ignorierliste [B]; Algorithmus [U] | Rechenintensiv, nur auf Abruf. Löschen frühestens in Phase 2. |
| B2 | **Mehr Autostart-Quellen** im Langsam-Check: Kontextmenü-/Shell-Erweiterungen, Browser-Erweiterungen (Anzahl) | Reiter „Kontextmenü“, „Browser“ [B] | Machbarkeit lesend und ohne Admin nicht geprüft [U]. |
| B3 | **Einfluss-Einstufung** (niedrig/mittel/hoch) für Autostart- und Hintergrundeinträge, aus eigenen Messwerten (CPU, I/O, RAM in der Stichprobe) statt aus einer Datenbank | Spalte „Einfluss“ [B]; Quelle bei CCleaner [U] | Passt zur Cleanii-Haltung „Messwert, Schwelle, Quelle“. |
| B4 | **Installierte Programme nach Größe**: Registry-Einträge mit Installationsort, verknüpft mit den Ordnergrößen aus dem Scan | Deinstallieren-Liste mit Größe [B] | Registry-Größenfeld ist oft leer; die Verknüpfung mit dem Scanbaum ist ein Vorteil von Cleanii. |
| B5 | **Wiederherstellungspunkte / Schattenkopien-Speicher** als Platzverbraucher | Systemwiederherstellung [B] | Lesbarkeit ohne Admin [U]. |
| B6 | **Treiberliste lokal** (Datum, Version, ohne Online-Abgleich) | Spalten des Driver Updater [B] | Konzept nennt „Treiberalter“, umgesetzt nur Grafiktreiber. |

**Priorität C – spätere Phase, mit CCleaners Sicherheitsmustern**

| # | Muster | Beleg |
|---|---|---|
| C1 | Analyse vor Bereinigung mit Hinweis „noch nichts gelöscht“ und Dateiliste; Ausnahmenliste; Wurzel- und Systemordner nie wählbar | 313, 1605, 1606 [B] |
| C2 | Hinweis, wenn eine laufende Anwendung die Bereinigung beeinträchtigt, statt stillschweigend zu überspringen | 8050–8058, 9806 [B] |
| C3 | Änderungen an Autostart/Diensten nur reversibel (Journal, Wiederherstellung) und mit Admin | `CTUJournal`, Reaktivierer [B/I] |
| C4 | Wiederherstellungspunkt vor riskanten Änderungen, übersprungen wenn kürzlich einer existiert | Driver Updater [B] |
| C5 | SSD-Warnung vor Überschreib-Funktionen | 2415 [B] |

### 4.3 Abgleich der Kategorie-Regeln (`CategoryRules.cs` gegen die Windows-Liste 3101–3156)

| CCleaner-Option | Cleanii-Regel heute |
|---|---|
| Papierkorb | vorhanden (`$Recycle.Bin`) |
| Temporäre Dateien (Windows, Benutzer) | vorhanden (`Windows\Temp`, `…\AppData\Local\Temp`) |
| Prefetch | vorhanden |
| Windows-Logs | vorhanden (`Windows\Logs`) |
| Alte Windows-Installation | vorhanden (`Windows.old`) |
| Windows-Update-Downloads | vorhanden (`SoftwareDistribution\Download`) |
| Minidumps | vorhanden; **MEMORY.DMP** (vollständiger Dump) nicht geprüft |
| Shader-Caches (DirectX, GL) | über Ordnernamen (`DXCache`, `GLCache`, `D3DSCache`, `ShaderCache`) |
| Absturzabbilder des Benutzers | vorhanden (`CrashDumps`) |
| Windows-Fehlerberichte (WER) | **keine Regel**; `ProgramData` zählt komplett als „Apps“ |
| Übermittlungsoptimierung (Delivery Optimization) | **keine Regel** |
| Font-Cache | **keine Regel** |
| Miniaturansichten (Thumbnail-Cache) | **keine Regel** |
| Ereignisprotokolle | fallen unter `Windows` → „System“ |
| CHKDSK-Fragmente (`FOUND.*`) | **keine Regel** |

Die Pfade der „keine Regel“-Zeilen sind Windows-Kenntnis, nicht aus CCleaner belegt. Vor dem Eintragen am Testrechner prüfen und Mengen messen, ob sich die Regel lohnt. Ein kuratierter Katalog wie bei CCleaner (127 App-Texte) ist wegen der Grenze „keine Regeldatenbank“ ausgeschlossen; sinnvoller sind **heuristische Funde** (Ordnernamen wie `Cache`, `logs`), wie sie Cleanii schon nutzt.

### 4.4 Nicht übernehmen

| Funktion | Begründung |
|---|---|
| Registry-Cleaner | verändert die Registry (Phase-1-Grenze); der Nutzen auf aktuellem Windows ist meine Einschätzung nach gering, das Risiko real. **Kein Beleg aus der Analyse.** |
| Software-/Driver-Updater | brauchen Netzwerk, Definitionsserver und Installationsrechte; widersprechen „lokal, kein Netzwerk“. |
| Performance-Optimierer („Schlafmodus“) | ändert Dienste, Aufgaben, Registry (Phase 1 verbietet). Die **Analyse-Hälfte** (Liste, Typ des Eintrags, Einfluss, Folgenhinweis) ist als B3/A5 übernehmbar. |
| Festplatten-Wiper, sicheres Löschen | zerstörend; nur als späteres Thema mit SSD-Warnung (C5). |
| Smart Cleaning, Zeitplan, Dienst im Hintergrund | dauerhafter Prozess mit Rechten; nicht Ziel von Cleanii. |
| Analytics, Datenfreigabe, Drittangebote, Testversion, Rabattbanner, Browser-Werbung | Gegenposition zu Cleanii. |
| Gesamtnote „Zustand des PCs“ als Farbfläche | widerspricht „Farbe nur bei Abweichung“; allenfalls als nüchterne Kennzahl („1 Befund braucht Aufmerksamkeit“ existiert bereits). |

---

## 5. Empfohlene Reihenfolge

1. **A1 Zeitstempel + A2 Export + A3 Suche** – schließen die im Konzept genannten Grenzen der Orte-Ansicht und bauen auf vorhandenen Daten auf.
2. **A4 Platzgewinn-Potenzial + A5 Folgenhinweise + A6 Regel-Lücken** – Nutzen für „Was belegt meine Festplatte?“ ohne neue Messungen.
3. **B4 Programme nach Größe + B3 Einfluss-Einstufung** – verbinden Scan und Langsam-Check, das ist Cleaniis eigener Vorteil.
4. **B1 Duplikat-Bericht** – größter Einzelgewinn bei Platzproblemen, aber rechenintensiv und eigenes Design nötig.
5. B2, B5, B6 nach Machbarkeitsprüfung.

---

## 6. Offene Punkte und Unsicherheiten

- **[U]** Aussehen und Verhalten des Disk Analyzers in 6.41; ob er Ordner zeigt, ist nur aus dem Fehlen passender Texte geschlossen.
- **[U]** Algorithmus des Duplicate Finders, Quelle der Einfluss-Einstufung, Bedeutung von „TUID“.
- **[U]** Ob die erweiterte Bereinigung und der Optimierer in der Gratis-Version nutzbar sind (Texte sprechen für Pro).
- **[I]** Funktionsweise „Schlafmodus“ (siehe 3.4), nicht per Disassembly bestätigt.
- **Nicht geprüft:** englische Standardtexte, andere Sprachdateien, Installer-Logik (`uninst.exe`), `CCleanerBugReport.exe`, `wa_3rd_party_host_*.exe`.
- **Nächster Schritt für mehr Tiefe:** Ghidra (`GHIDRA_INSTALL_DIR`) für REA konfigurieren; das würde Dekompilierung von Optimierer, Reaktivierer und Driver Updater erlauben. Ich habe dafür nichts installiert, weil es nicht beauftragt war.

---

## 7. Umsetzungsstand (2026-10-16)

Alle Kandidaten der Prioritäten A und B sind umgesetzt; Priorität C und die Liste „nicht übernehmen“ nicht. Einzelheiten und Echtläufe stehen in `KONZEPT.md` („Stand Übernahmen aus der CCleaner-Analyse“).

| # | Idee | Stand | Abweichung oder Vorbehalt |
|---|---|---|---|
| A1 | Zeitstempel, Sortierung, „alt und groß“ | umgesetzt | Nur die Änderungszeit; Erstellung und Zugriff bewusst nicht (Zugriffszeit unzuverlässig, Speicherbedarf). Die offene Prüfung ist bestätigt: `FileSystemEntry` liefert die Zeit direkt aus der Verzeichnisauflistung, der Scan braucht keinen Zusatzaufruf. |
| A2 | Export | umgesetzt | CSV (Orte, Typen, Programme, Duplikate) und JSON (Langsam-Check). Der Speichern-Dialog ist nicht bedient worden. |
| A3 | Suche | umgesetzt | Im gewählten Ordner und darunter, die 500 größten Treffer; Ordner-Treffer ohne Typ-Aufteilung. |
| A4 | Platzgewinn-Potenzial | umgesetzt | Nur Papierkorb, Temp und Caches, alte Windows-Installation. |
| A5 | Folgenhinweise | umgesetzt für die Typen | Eigene Texte. Befunde haben bereits Erklärungen. |
| A6 | Regel-Lücken | umgesetzt | WER, Delivery Optimization, Font-Cache, Thumbnails, `MEMORY.DMP`, `found.000`. Wirkung auf `C:\` nicht gemessen. |
| B1 | Duplikat-Bericht | umgesetzt | Echtlauf: 2.192 Gruppen in 65 s. |
| B2 | Shell- und Browser-Erweiterungen | umgesetzt | Echtlauf: 33 Browser-Erweiterungen, 5 Drittanbieter-Erweiterungen im Kontextmenü. |
| B3 | Einfluss-Einstufung | umgesetzt | Last im Betrieb aus der Stichprobe, nicht Startdauer (die braucht Administratorrechte). |
| B4 | Programme nach Größe | umgesetzt | Echtlauf `C:\`: 121 Programme, 86 gemessen. |
| B5 | Schattenkopien-Speicher | umgesetzt, nur mit Administratorrechten messbar | Ohne Rechte „nicht gemessen“ mit Grund (beobachtet). Die Messung mit Rechten ist nicht getestet. |
| B6 | Treiberliste lokal | umgesetzt | Platzhalterdaten und Windows-Standarddaten werden nicht gewertet (am Echtlauf gefunden). |

---

## 8. Anhang – Reproduktion und Hashes

Auswerteskripte, die dekodierte Textliste und die String-Auszüge liegen **außerhalb dieses Repos** in `D:\Workspace\Cleanii-RE\analysis\` (Herstellertexte gehören nicht ins Cleanii-Repo).

| Datei | SHA-256 |
|---|---|
| `Lang\lang-1031.dll` | `766af5fa17a56e2e3771762a811685cea165201a3e98a647e35c1b248c957b92` |
| `CCleaner64.exe` | `30de2996906c923de267a822cd92f8f2b1ad2d52afe23c75e37c653858a5fe53` |
| `CCleaner.exe` | `7567872860fde3cbdbf849e038c8684bd6eeb2405797b14062c05ec433ff6733` |
| `CCleanerDU.dll` | `7defbef2336e936caac550fb3a43b1bb4d0f4ce92db45a39ddcad09923b0147c` |
| `CCleanerPerformanceOptimizer.dll` | `b6e1268e8934af4a36ddde48dae69fd6d5e4c605fc82a219edee6ce8ab7ba140` |
| `CCleanerPerformanceOptimizerService.exe` | `4b447a27c2edaa3fe49460e8922f238162ba2ec863164ba26f22ea6cad5ccc49` |
| `CCleanerReactivator.dll` | `40d7c11d6df8feaa9b03e84e8884bb5718f3d0d7e8177d38b77d9cc2aea8c01a` |
| `CCleanerReactivator.exe` | `ddb1a2d74d6ccdcc9a0aae2fd226a7dd5697be738dcfbc3ecded14ee8d3cb065` |

**Nachstellen:** REA `inspect_pe_resources` auf `lang-1031.dll`, jede RT_STRING-Ressource (Typ 6) über die Dateibereiche lesen, 16 Strings je Block mit 2-Byte-Länge davor, ID = (Blockname − 1) × 16 + Index.

**Text-ID-Spannen** (zum Nachschlagen): 2xx–7xx Hauptmenü und Cleaner, 1401–1478 Einstellungen, 1501–1525 Cookies, 1601–1649 Ein-/Ausschließen, 1801–1837 Smart Cleaning, 1901–1960 Zeitplan, 2001–2762 Extras, 3001–3214 Cleaner-Optionen, 3601–3822 Registry, 3905–3945 Disk Analyzer, 8200–8400 Health Check, 8481–8752 und 9582–9732 Driver Updater, 8950–9200 Performance-Optimierer, 9202–9346 und 9931–10071 App-Bereinigung, 9887–9930 Software Updater.
