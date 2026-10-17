# HokiiCleaner – Konzept (Phase 1: Analyse, dazu Aufräumen und ausgewählte Dateien in den Papierkorb)

## Ziel
Zwei Fragen beantworten:
1. **Was belegt meine Festplatte?** – nach Typen (wie Android „Speicher“) und nach Orten (Ordnerbaum).
2. **Warum ist mein PC langsam?** – nachvollziehbarer Befund-Bericht mit Messwerten.

## Harte Grenze: nur lesen, mit genau drei benannten Ausnahmen
- Speicher-Analyse und Langsam-Check verändern nichts: keine Funktion, die Dateien, Registry, Dienste oder Einstellungen verändert oder löscht. Scanner und Core (`HokiiCleaner.Core`) bleiben rein lesend.
- Erlaubt ohne Rückfrage: „Im Explorer zeigen“, „Pfad kopieren“, Export des Berichts (JSON/CSV, Speichern-Dialog).
- Alles Verändernde liegt in `HokiiCleaner.Actions` und geschieht nur nach Vorschau und ausdrücklicher Bestätigung:
  1. **Aufräumen:** endgültiges Löschen, aber nur Dateien aus einer festen Liste von Temp-Ordnern und Caches (siehe „Stand Aufräumen“).
  2. **Auswahlkorb:** Dateien und Ordner, die der Benutzer selbst vormerkt, werden in den **Papierkorb** verschoben (wiederherstellbar). Nie etwas aus der Schutzliste (Windows, Programme, Benutzerprofil, HokiiCleaner selbst, vom Benutzer geschützte Ordner), nie Verknüpfungen, nie etwas, das der Papierkorb des Laufwerks nicht aufnehmen kann (Windows würde es sonst endgültig löschen).
  3. **Papierkorb-Ansicht:** Wiederherstellen (überschreibt nichts) und Leeren (endgültig, eigene Bestätigung).
- Geschrieben wird außerdem nur in den eigenen Ordner `%LOCALAPPDATA%\HokiiCleaner`: Protokolle (`logs`), gespeicherte Stände (`history`, nur auf Klick „Stand speichern“) und `settings.json` (geschützte Ordner, nur beim Ändern der Liste).

## Ansichten
| Ansicht | Inhalt |
|---|---|
| Übersicht | Laufwerk wählen, Gesamtbalken (belegt/frei) gestapelt nach Typ, Kurzbefund „Langsam-Check“ |
| Typen | Kategorien mit Größe und Anteil, Drilldown in die größten Dateien je Kategorie |
| Orte | Ordnerbaum nach Größe sortiert (Größe, Anteil, Dateizahl), Treemap, Drilldown; Typ-Filter wirkt auch hier |
| Langsam-Check | Befunde nach Schweregrad (kritisch / Warnung / Info), je mit Messwert, Schwelle, Erklärung |
| Programme, Duplikate | siehe „Stand Übernahmen aus der CCleaner-Analyse“ (Duplikate: Kopien lassen sich seit „Stand Auswahlkorb“ vormerken) |
| Entwickler | neu erzeugbare Projektordner (node_modules, Python-Umgebungen, Build-Ausgaben), nach Alter des Projekts filterbar |
| Papierkorb | Inhalt, Größe, Wiederherstellen, Leeren |
| Verlauf | gespeicherte Stände eines Scans und Vergleich mit dem aktuellen Scan |

Typ ↔ Ort sind verknüpft: Klick auf eine Kategorie zeigt, wo sie liegt; Klick auf einen Ordner zeigt, aus welchen Typen er besteht.

## Kategorien (Windows-Entsprechung zum Android-Screenshot)
Videos, Bilder, Audio, Dokumente, Archive, Installer und Abbilder (.msi/.iso/.img), KI-Modelle (.safetensors/.gguf/.ckpt/.pt/.onnx; ergänzt, weil sie auf dieser Platte stark ins Gewicht fallen), Apps (Program Files, AppData\Local\Programs), System (Windows, `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`), Temp & Caches, Alte Windows-Installation (`Windows.old`), Virtuelle Disks (WSL/Docker/VM: `.vhd(x)`, `ext4.vhdx`), Papierkorb, Sonstiges. Die Zuordnung erfolgt zuerst über Pfadregeln (System/Apps/Caches), dann über die Dateiendung.

## Scan-Engine (`HokiiCleaner.Core`)
- Parallel, abbrechbar, mit Fortschritt; Ergebnis als kompakter Baum (Ordner aggregiert, Dateien als Arrays, Top-N je Kategorie).
- **Belegung auf dem Datenträger** (Cluster-Größe, komprimierte/sparse Dateien) zusätzlich zur Dateigröße.
- **Hardlinks** (z. B. WinSxS) über Datei-ID nur einmal zählen.
- **Reparse Points/Junctions/Symlinks** nicht folgen (Schleifen, Doppelzählung).
- **Cloud-Platzhalter** (OneDrive „nur online“) zählen 0 Byte auf dem Datenträger.
- **Nicht lesbare Pfade** (fehlende Rechte, `System Volume Information`) erscheinen als eigener Eintrag „nicht lesbar“ mit Größe unbekannt – nie stillschweigend weglassen.
- Später optional: MFT-Scan (Admin) für Sekunden-Scans.

## Langsam-Check (alles lesend)
| Signal | Quelle (geplant, noch zu verifizieren) |
|---|---|
| Laufwerk | `DriveInfo` (Füllstand), Storage-WMI (`MSFT_PhysicalDisk`: HDD/SSD, Health; `MSFT_StorageReliabilityCounter`: Temperatur, Verschleiß, Fehler), Performance Counter (Disk-Queue, Latenz) |
| Autostart & Boot | Registry `Run`/`RunOnce` + `StartupApproved`, Autostart-Ordner, Aufgabenplanung (Logon/Boot-Trigger), Dienste mit Autostart, Ereignisprotokoll `Diagnostics-Performance` (Boot-Dauer, Verzögerer) |
| Laufende Prozesse | Stichprobe über 5–10 s: CPU, RAM, Disk-I/O pro Prozess; RAM-Druck, Commit/Auslagerungsdatei |
| System-Konfiguration | Energieplan, Hintergrund-Apps, ausstehende Updates/Neustart, Treiberalter, Uptime |

Jeder Befund nennt Messwert, Schwelle und Quelle. Schwellen sind Startwerte und werden an echten Rechnern kalibriert. Es gibt keinen „Fix“-Button.

## UI-Designsprache (Vorschlag, eigenständig – keine Anlehnung an CCleaner/Android-Optik)
**Richtung: „Messinstrument“** – dunkel, ruhig, dicht an den Daten. Betrachtet wird ein Datenträger wie ein Messobjekt: Zahlen sind die Hauptdarstellung, Dekoration gibt es nicht. (Verworfen: „luftig/editorial“ – zu wenig Datendichte; „Glas/Acryl“ – Kontrast und Lesbarkeit leiden bei vielen Zahlen.)

**Leitprinzipien**
- **Farbe nur bei Abweichung.** Alles Normale bleibt neutral; nur Warnung (Amber) und Kritisch (Rot) sind farbig. Ein Befund-Bericht ohne Probleme ist bewusst „still“.
- **Ein Fokus, zwei Ansichten.** Auswahl (Typ, Ordner, Befund) bleibt beim Wechsel zwischen Typen und Orten erhalten und hebt in der jeweils anderen Ansicht die zugehörigen Segmente hervor, der Rest tritt zurück.
- **Das Laufwerk als Messband.** Der Gesamtbalken trägt eine feine Skala (Tick-Marks in GB), Segmente liegen dicht ohne Lücken; Hover zeigt Kategorie, Größe, Anteil.
- **Befund als Protokollzeile.** Messwert, Schwelle und kleine Skala in einer Zeile, Erklärung klappt darunter auf.
- Flache Hierarchie: Trennung über Abstand und 1-px-Haarlinien statt verschachtelter Karten.

**Tokens (Startwerte, in einem `ResourceDictionary` zentral)**
| Rolle | Wert |
|---|---|
| Hintergrund / Fläche / Fläche erhöht | `#0E1013` / `#151921` / `#1B2029` |
| Haarlinie | `#262C38` |
| Text / Text gedimmt | `#E6E9EF` / `#8D96A7` |
| Akzent (Auswahl, Fokus, Primäraktion) | `#B7F34A` |
| Warnung / Kritisch | `#F5B94A` / `#FF6B6B` |
| Radien | 6 px (Controls), 10 px (Panels) |
| Raster | 4-px-Basis, Gruppen eng (8), zwischen Gruppen luftig (24) |
| Typo-Skala | 11 / 12 / 13 / 16 / 24 / 40 px; Segoe UI Variable, Zahlen tabellarisch (Mono-Schrift oder OpenType-`tnum`, noch zu prüfen) |

- **Kategorienfarben:** 12 unterscheidbare, gedämpfte Töne, die auf `#151921` Kontrast halten; Palette wird beim Bau mit Validierung festgelegt (nicht nach Gefühl).
- **Zustände** für alles Interaktive: Hover, Fokus (sichtbar, per Tastatur), Aktiv, Deaktiviert, Lädt; Übergänge 120–200 ms.
- **Leer-, Lade-, Fehlerzustände** sind Teil des Designs (z. B. Scan läuft: Balken füllt sich live; „nicht lesbar“ als gestreiftes Segment).
- **Theme:** zuerst nur Dunkel; Hell ist ein späterer Schritt, die Tokens sind dafür von Anfang an rollenbasiert.
- **UI-Textsprache:** Deutsch (Annahme, siehe offene Punkte).

## Berechtigungen
Start ohne Admin. Was ohne Admin fehlt (volle Ordnertiefe, Teile von S.M.A.R.T./Ereignisprotokoll), wird im Bericht ausdrücklich benannt. Der Knopf „Als Administrator neu starten“ (Statuszeile, nur ohne Adminrechte) startet HokiiCleaner mit Windows-Rückfrage neu; ein laufender Scan geht dabei verloren. HokiiCleaner erhöht seine Rechte nie von selbst.

## Architektur
- `HokiiCleaner.Core` – Scan, Kategorisierung, Befund-Regeln; keine UI-Abhängigkeit.
- `HokiiCleaner.Actions` – alles, was verändert (Aufräumen, Papierkorb); kennt `HokiiCleaner.Core` nicht.
- `HokiiCleaner.App` – WPF, MVVM.
- `HokiiCleaner.Tests` – Unit-Tests für Kategorisierung, Hardlink-/Reparse-Behandlung, Befund-Regeln.
- Alles lokal, keine Telemetrie, keine Netzwerkzugriffe.

## Meilensteine
1. Scan-Engine + Konsolen-Test (Zahlen mit Explorer/`dir` gegenprüfen)
2. Typen-Ansicht
3. Orte-Ansicht (Baum, Treemap)
4. Langsam-Check
5. Export, Feinschliff

## Stand Meilenstein 1 (Scan-Engine)
Umgesetzt in `src/HokiiCleaner.Core/Scanning`, getestet mit 12 Unit-Tests (`tests/HokiiCleaner.Tests`) und dem Konsolen-Host `src/HokiiCleaner.Cli`.
- Gegenprobe mit PowerShell auf einem 201-Dateien-Baum: Dateizahl, Ordnerzahl, logische Größe und cluster-gerundete Belegung stimmen exakt.
- Belastungstest `C:\` (2,5 Mio. Dateien, 500 000 Ordner): 38,8 s kalt, 14,1 s warm. 594 Ordner ohne Adminrechte nicht lesbar, als „nicht lesbar“ gemeldet. Summe 765,1 GB gegenüber 801,1 GB laut Windows (beides in GiB; die Windows-Angabe sind 860,06 Mrd. Byte); die Lücke von rund 36 GB passt zu den nicht lesbaren Ordnern (z. B. `System Volume Information`), ist aber nicht einzeln belegt.

**Bekannte Grenzen**
- Hardlink-Entduplizierung nur in `Windows\WinSxS` (pro Datei ein Handle-Zugriff, daher gezielt); anderswo werden Hardlinks mehrfach gezählt.
- Belegung normaler Dateien ist auf die Clustergröße gerundet (Näherung; sehr kleine, in der MFT gespeicherte Dateien können in Wirklichkeit 0 belegen). Komprimierte und Sparse-Dateien nutzen die echte Belegung.
- OneDrive-Platzhalter (0 Byte Belegung) sind implementiert, aber nicht getestet.
- Scan als Administrator nicht getestet.

## Stand Meilenstein 2 (Kategorisierung und Typen-Ansicht)
- **Core:** `Categorization/` ordnet jede Datei zu (Pfadregeln, tiefster Treffer gewinnt und wird vererbt, dann Dateiendung), inklusive Top-100-Dateien je Kategorie. 20 weitere Tests, insgesamt 32.
- **Realtest `C:\`:** Größte Kategorien KI-Modelle 151 GB, Apps 148 GB, Sonstiges 102 GB, Temp und Caches 98 GB. „Temp und Caches“ besteht tatsächlich aus Caches (`pip\cache`, `uv\cache`, Browser- und Shader-Caches).
- **App:** Typen-Ansicht im „Messinstrument“-Design: Messband mit Skala, schraffiertem Segment für nicht zugeordneten Platz, Kategorienliste, größte Dateien je Kategorie, Warnzeile bei unvollständigem Scan. Kategorienfarben in OKLCH berechnet und geprüft (Kontrast ≥ 3,1:1, kleinster Abstand 0,08).
- **Diagnose:** `HokiiCleaner.App.exe --scan <Pfad> --screenshot <Datei.png>` rendert die Ansicht nach dem Scan als PNG und beendet die App.

**Noch nicht geprüft:** Hover-Verknüpfung zwischen Messband und Liste, Klick-Auswahl, Doppelklick „im Explorer zeigen“, Abbrechen-Schaltfläche und Laufwerkswechsel (alles nur per Code geschrieben, nicht bedient). Auch ungetestet: schmale Fenster, UI-Tests.

## Stand Meilenstein 3 (Orte-Ansicht)
- **Core:** `CategoryAnalyzer.Compose` liefert in einem Durchlauf die Belegung je Kategorie für jeden direkten Unterordner und die Dateien direkt im Ordner (Grundlage für Liste, Treemap und Typ-Filter). `SquarifiedTreemap` berechnet das Treemap-Layout. 13 neue Tests, insgesamt 45.
- **App:** Reiter TYPEN und ORTE unter dem Messband. Orte: Pfad mit Klick-Navigation und „↑“, Ordnerliste nach Größe (Doppelklick oder Enter öffnet, Rücktaste geht hoch), Treemap, Zusammensetzung des gewählten Ordners, Filter „Nur {Typ}“. In der Typen-Ansicht springt „Wo liegt das?“ in die Orte-Ansicht, gefiltert auf den gewählten Typ. Nicht lesbare Ordner bleiben mit Hinweis sichtbar.
- **Diagnose:** `--view types|places|places-filtered` und `--open <Ordner>` stellen die Ansicht für Screenshots ein.

**Noch nicht bedient/geprüft:** Klicks in Breadcrumb, „↑“, Doppelklick und Enter in Liste und Treemap, Hover-Verknüpfung von Treemap und Liste, „Wo liegt das?“, Umschalten per Reiter. Gerendert und gesehen: Orte-Ansicht ungefiltert, gefiltert, in einem Ordner mit nicht lesbaren Unterordnern.

**Bekannte Grenze:** Bei aktivem Typ-Filter gibt es keine gefilterte Dateizahl; die Zeilen zeigen dann nur den Anteil.

## Stand Meilenstein 4 (Langsam-Check)
- **Core (`Diagnostics/`):** Messungen sind getrennt von der Auswertung. `SlownessRules` ist eine reine Funktion über den Rohdaten (voll testbar), jede Prüfung nennt Messwert, Schwelle, Erklärung und Quelle. `SlownessAnalyzer` misst ohne Schreibzugriff. Eine Messung, die scheitert, erscheint als „nicht gemessen“ mit Grund; nur erwartbare Fehler (Rechte, WMI, Dateisystem) werden so umgewandelt, alles andere und Abbruch laufen nach oben. 72 neue Tests, insgesamt 117.
- **Gemessen (ohne Admin):** Füllstand je Laufwerk, Plattentyp (HDD/SSD) und Health, Laufwerk→Platte, Plattenauslastung und Antwortzeit (5-s-Stichprobe, Leistungsindikatoren), Autostart (Run-Schlüssel 64/32 Bit, Autostart-Ordner, aktiv/deaktiviert), geplante Aufgaben mit Anmelde-/Startauslöser (CIM), Dienste mit Autostart, Prozessorlast gesamt und je Programm, größte Speicher- und Platten-I/O-Verbraucher, Arbeitsspeicher, Commit und Auslagerungsdatei, Energieplan, Laufzeit und Schnellstart, ausstehender Neustart, Grafiktreiber.
- **Nicht messbar ohne Admin, im Bericht als solche gekennzeichnet:** S.M.A.R.T. (Temperatur, Verschleiß; Zugriff auf `MSFT_StorageReliabilityCounter` verweigert) und die Dauer des Systemstarts (Ereignisprotokoll `Diagnostics-Performance` verweigert). Auch mit Admin sind beide in dieser Version nicht umgesetzt, und das steht so im Bericht.
- **Bewusst gestrichen:** „Hintergrund-Apps“ (keine verlässliche lokale Quelle unter Windows 11) und „ausstehende Updates“ (bräuchte Netzwerkzugriff; nur Neustart-Hinweise werden gelesen).
- **Annahme mit Beleg, aber nicht von Microsoft dokumentiert:** Aktiviert/deaktiviert bei Autostart-Einträgen steckt im niedrigsten Bit des ersten Bytes von `StartupApproved` (gerade = aktiviert). Auf dem Testrechner lief nur das Programm mit geradem Wert, alle mit ungeradem nicht. Einzelne Einträge sollten beim ersten Einsatz im Task-Manager gegengeprüft werden.
- **Schwellen** (z. B. 15 % frei, 70 % CPU, 10 Autostart-Einträge) sind Startwerte (`SlownessThresholds`) und nicht kalibriert. Der Befund „34 Dienste außerhalb von Windows“ ist eine Näherung für Drittanbieter (Pfad außerhalb des Windows-Ordners).
- **App:** Oben die Navigation SPEICHER | LANGSAM-CHECK. Langsam-Check zeigt eine Kennzahl („1 Befund braucht Aufmerksamkeit“), Gruppen nach Schwere, aufklappbare Befunde mit Schwelle, Erklärung, Einzelposten und Quelle; „unauffällig“ ist eingeklappt. Farbe nur bei Warnung und Kritisch.
- **Pakete:** `System.Management` und `System.Diagnostics.PerformanceCounter` (beide von Microsoft, Version 10.0.13; laufen unter net8.0).
- **Diagnose:** `HokiiCleaner.Cli slow [--window N] [--all]` und `HokiiCleaner.App.exe --view slow|slow-expanded [--screenshot Datei.png]`.
- **Gefundene und behobene Fehler:** `Properties["Execute"]` wirft bei geplanten Aufgaben ohne dieses Feld (alle Aufgaben waren „nicht messbar“); Listen- und Befundtitel waren schwarz auf dunkel, weil `ListBox`/`ToggleButton` eine eigene Standard-Textfarbe haben.

**Noch nicht bedient/geprüft:** Aufklappen per Klick und Tastatur, Einklappen von „unauffällig“, „Abbrechen“ während der Messung, Wechsel zwischen den Bereichen im laufenden Betrieb. Nur gerendert und gesehen: Ergebnis ohne und mit aufgeklappten Befunden. Nicht getestet: Lauf als Administrator, Rechner mit HDD, mit knappem Speicher oder hoher Last (die Regeln dafür sind nur durch Tests mit künstlichen Werten abgedeckt).

## Stand Redesign „Sonar, Glas, Dunkel und Hell“
- **Theme-System:** `Themes/Dark.xaml` und `Light.xaml` (gleiche Schlüssel), Umschalten zur Laufzeit über `ThemeService` (Wie Windows, Hell, Dunkel; folgt der Windows-Einstellung). Die Wahl steht in `%LOCALAPPDATA%\HokiiCleaner\settings.json` (die einzige Datei, die HokiiCleaner von sich aus schreibt; Exporte entstehen nur, wenn der Benutzer sie über „Exportieren …“ und den Speichern-Dialog auslöst).
- **Glas:** eigener Lichtverlauf-Hintergrund, halbtransparente Karten mit Randlicht, eigene Titelleiste, Seitenleiste. Kein echtes Weichzeichnen des Desktops (in WPF nicht performant möglich). Schrift: Segoe UI Variable statt der Mockup-Schrift.
- **Sonar:** `SonarDial` zeichnet Ordner als Ring (Kapazität = voller Kreis, schraffierter nicht zugeordneter Platz, äußerer Typ-Ring, Skala, Aufbau-Animation), mit Hover-Anzeige in der Mitte, Typ-Chips, Info-Karte und größten Dateien. Geometrie in `Core` (`RadialLayout`, `FileRanking`). 141 Tests.
- **Diagnose:** `--theme light|dark|system`, `--view sonar|types|places|slow`.

**Nicht bedient/geprüft:** Klicks, Hover, Doppelklick im Ring, Typ-Chips, das Erscheinungsbild-Menü (Popup), Maximieren der rahmenlosen Fenster, Windows 10. Nicht umgesetzt: „Stand von heute“ (kein gespeicherter letzter Scan).

## Stand Orte-Ansicht als Mini-Browser
- **Core:** `FolderComposition.OwnFiles` liefert die Dateien direkt im Ordner mit Kategorie; `HeavyTrail` folgt vom Ordner aus dem jeweils dominierenden Eintrag (mehr als die Hälfte seines Ordners, höchstens 8 Ebenen) bis zu Ordner oder Datei, `ChildComposition.Trail` hängt sie an jeden Unterordner. 4 neue Tests, insgesamt 145.
- **App (Orte):** Die Sammelzeile „Dateien in diesem Ordner“ ist durch Einzeldateien ersetzt (die 500 größten je Ordner; der Rest als eine Sammelzeile am Listenende, Dateien ohne Belegung erscheinen nicht). Ordner und Dateien tragen Mappen- bzw. Blatt-Symbol in der Kategorienfarbe. Ordnerzeilen zeigen Dateizahl, Ordnerzahl und eine Spur zum größten Inhalt („↳ app › ckpts · 81,88 GB“), ohne dass man hineinklicken muss. Rechts steht für die Auswahl der volle Pfad, Belegung und Dateigröße sowie „Im Explorer zeigen“ und „Pfad kopieren“. Doppelklick und Enter öffnen einen Ordner bzw. zeigen eine Datei im Explorer; die Treemap enthält ebenfalls einzelne Dateien. Typ-Filter wirkt auch auf die Dateien.
- **Gerendert und gesehen:** Ordner mit Spur, Ordner mit Einzeldateien, Ordner mit mehr als 500 Dateien (Sammelzeile), Typ-Filter, Dunkel und Hell.
- **Nicht bedient/geprüft:** Doppelklick und Enter auf Dateien, „Im Explorer zeigen“, „Pfad kopieren“ (nur per Code geschrieben).
- **Grenzen:** Keine aufklappbaren Ebenen in der Liste (Navigation per Ordner betreten). Bei aktivem Typ-Filter entfallen Spur und Dateizahl der Ordner. (Datum der letzten Änderung, Sortierung und Suche sind seit „Übernahmen aus der CCleaner-Analyse“ umgesetzt, siehe unten.)

## Stand Übernahmen aus der CCleaner-Analyse
Grundlage: `docs/CCLEANER-ANALYSE.md`, Priorität A und B. Priorität C (Reinigungsmuster) und die Liste „nicht übernehmen“ bleiben draußen. 196 Tests (51 neu).

**Scan und Orte**
- **Änderungsdatum:** `FileEntry.LastWriteUtc`, `DirectoryNode.NewestWriteUtc` (jüngste Änderung im Inhalt). Erstellungs- und Zugriffszeit werden bewusst nicht gespeichert (Zugriffszeit auf NTFS oft abgeschaltet, beides kostet bei Millionen Dateien Speicher). Orte: Datum in jeder Zeile, Sortierung nach Größe/Name/Geändert, Filter „Seit 1 Jahr unverändert“ (zeigt z. B. große, alte Dateien).
- **Suche:** `TreeSearch` im Speicher, ab zwei Zeichen, im gewählten Ordner und darunter, die 500 größten Treffer (alle werden gezählt). Treffer zeigen ihren Ort; „Im Ordner zeigen“ springt dorthin. Ordner-Treffer haben keine Aufteilung nach Typen und fallen bei aktivem Typ-Filter weg.
- **Export:** CSV (Semikolon, UTF-8 mit BOM, Schutz vor Formel-Injektion in Zellen, die mit `= + - @` beginnen) für Orte-Liste, größte Dateien eines Typs, Programme, Duplikate; JSON für den Langsam-Check.
- **Platzgewinn-Potenzial:** Summe aus Papierkorb, Temp und Caches, alter Windows-Installation, als klickbare Chips in Sonar, Typen und Orte. Nur Orientierung, kein Löschen.
- **Folgenhinweise** je Typ (eigene Texte) in der Typen-Ansicht.
- **Regeln:** Fehlerberichte (WER), Delivery Optimization, Font-Cache, Thumbnail-Cache, `MEMORY.DMP` (neu: Regel für eine einzelne Datei an einem Pfad), `found.000`. Die Pfade stammen aus Windows-Kenntnis; ihre Wirkung auf `C:\` ist nicht gemessen.

**Neue Ansichten**
- **Programme** (Seitenleiste): Registry-Liste, verknüpft mit den im Scan gemessenen Ordnergrößen; ohne Messung die Schätzung des Installers (≈); Sammel- und Windows-Ordner werden nicht als Installationsort akzeptiert; fehlt `InstallLocation`, wird der Ordner aus dem Symbolpfad abgeleitet und so gekennzeichnet. Echtlauf `C:\`: 121 Programme, 86 gemessen.
- **Duplikate** (Seitenleiste): Größe → Anfang und Ende (64 KiB) → SHA-256 über die ganze Datei, im Ordner der Orte-Ansicht, Mindestgröße 1/10/100 MB, mit Fortschritt und Abbruch. Cloud-Platzhalter werden nicht gelesen, Hardlinks auf dieselbe Datei zählen nicht als Kopie, geänderte oder gesperrte Dateien erscheinen als Hinweis. Nur Bericht, nichts wird gelöscht. Echtlauf `D:\Workspace` (8.969 Dateien ab 1 MB): 2.192 Gruppen, bis zu 28,2 GB durch Kopien belegt, 65 s.

**Langsam-Check** (neue Befunde)
- Erweiterungen im Explorer-Kontextmenü (Drittanbieter) und Browser-Erweiterungen (Chrome, Edge, Brave, Vivaldi: alle installierten; Firefox: aktive, vom Benutzer installierte).
- Last der Autostart-Programme (niedrig/mittel/hoch) aus der Stichprobe, zugeordnet über den Prozessnamen. Das ist die Last im Betrieb, nicht die Verlängerung des Starts.
- Alter der Gerätetreiber, nur lokal. Platzhalter- und Windows-Standarddaten (1968, 21.6.2006, Versionen `10.0.…`) zählen nicht (am Echtlauf gefunden, 28 von 85 Treibern betroffen).
- Schattenkopien (Wiederherstellungspunkte): braucht Administratorrechte. Ohne sie meldet WMI „Initialization failure“; der Befund erscheint dann als „nicht gemessen“ mit Grund.

**Gefundene und behobene Fehler:** Im Duplikat-Finder wurden Zähler in den Argumenten von `progress?.Report(…)` erhöht und liefen ohne Fortschrittsanzeige nicht (`HashedBytes` blieb 0); die Suche entsorgte ihre `CancellationTokenSource`, während eine laufende Suche das Token noch las.

**Noch nicht bedient/geprüft:** Speichern-Dialog und Dateischreiben aller Exporte (Logik und Format sind getestet, der Dialog nicht), Klicks auf die neuen Schaltflächen und Chips, „Im Ordner zeigen“ aus Treffern und Duplikaten, Abbrechen einer Duplikat-Suche in der Oberfläche, der Schattenkopien-Befund mit Administratorrechten, Windows 10, schmale Fenster.

**Grenzen:** Duplikat-Suche liest bei vielen großen Dateien lange (SHA-256 über die ganze Datei). Ein Duplikat ist nicht automatisch überflüssig. Programmgrößen aus abgeleiteten Ordnern sind Näherungen. Opera und der Firefox-Standard werden bei den Browser-Erweiterungen nicht erfasst.

## Stand Aufräumen (Temp und Caches löschen)
**Gebaut:** neues Projekt `src/HokiiCleaner.Actions` (nur Löschlogik, kein UI) und der Bereich AUFRÄUMEN (dritter Reiter). Er prüft beim ersten Öffnen und per „Neu prüfen“ nur lesend, zeigt je Ziel Größe, Dateizahl, Folge des Löschens und die drei größten Dateien. Gelöscht wird erst nach „Endgültig löschen …“ und einer zweiten Bestätigung in der Aktionsleiste; endgültig, nicht über den Papierkorb.

**Ziele (feste Liste in `CleanTargets`, keine frei wählbaren Pfade):** Temp-Ordner des Benutzers und `C:\Windows\Temp` (vorgewählt, nur Dateien, die seit 7 Tagen unberührt sind), pip-, uv-, npm-Cache, NVIDIA-Shader-Cache und Explorer-Vorschaubilder (nicht vorgewählt). Auf diesem Rechner stecken im Temp-Ordner nur etwa 2 GB, davon wenig älter als 7 Tage; die große Menge liegt in den Caches.

**Schutzmaßnahmen:**
- Wurzelordner werden geprüft (`RootGuard`): vollständiger Pfad, mindestens zwei Ebenen unter dem Laufwerk, kein Reparse Point, erwarteter Ordnername (fängt falsch gesetzte `TEMP`-Variablen ab). Ungeeignete Wurzeln werden abgelehnt und mit Begründung angezeigt.
- Jeder Pfad wird vor dem Löschen erneut gegen die Wurzel geprüft; Junctions und Symlinks werden weder betreten noch angefasst.
- Alter = jüngster Zeitstempel aus Schreiben, Anlegen und Zugriff (entpackte Dateien mit altem Änderungsdatum zählen so als neu).
- Ausnahmeliste je Ziel (`ExcludedFolders`, relativ zur Wurzel): `%TEMP%\claude` (Arbeitsordner von Claude Code, auch anderer Sitzungen) wird nie betreten, gezählt oder gelöscht. Ein gleichnamiger Ordner tiefer im Baum ist nicht ausgenommen.
- Ordner bleiben stehen; nur Inhalt und danach leere, alte Unterordner werden entfernt.
- Gesperrte oder nicht zugreifbare Dateien werden übersprungen und gemeldet, nie stillschweigend ignoriert. Kein Administratorrecht, keine Rechteerhöhung.
- Analyse und Löschen laufen durch denselben Code, damit die Vorschau dem Löschen entspricht.
- Protokoll je Lauf unter `%LOCALAPPDATA%\HokiiCleaner\logs\aufraeumen-*.txt` (Summen je Ziel und alle übersprungenen Dateien; einzelne gelöschte Dateien werden nicht aufgelistet).
- Das Ergebnis zeigt den am freien Platz gemessenen Zuwachs und die Summe laut Dateigrößen. Beides kann abweichen (andere Schreiber, geteilte Hardlinks, z. B. uv).

**Diagnose:** `--view clean` (prüft nur) und `--view clean-confirm` (öffnet nur die Bestätigungsleiste) mit `--screenshot`; beide löschen nie.

**Tests:** `CleanEngineTests` (Alter, Junctions, Muster, gesperrte und schreibgeschützte Dateien, unlesbare Ordner, Abbruch, Wurzelprüfung, Protokoll) laufen ausschließlich in Testordnern unter `%TEMP%`.

**Weitere Ziele (seit „Stand Auswahlkorb“, alle nicht vorgewählt):** Browser-Caches von Chrome, Edge, Brave (je Profil: `Cache`, `Code Cache`, `GPUCache`) und Firefox (`cache2`), NuGet-Zwischenspeicher und NuGet-Paketordner, Yarn- und Gradle-Cache, Absturzabbilder des Benutzers, Windows-Fehlerberichte (WER), sowie mit Administratorrechten Windows-Update-Downloads (nur Dateien älter als 7 Tage), Übermittlungsoptimierung und Minidump. Ziele, deren Ordner es auf dem Rechner nicht gibt, erscheinen nicht. Nicht aufgenommen: `Windows.old` (Windows-Datenträgerbereinigung nutzen) und `MEMORY.DMP` (liegt direkt im Windows-Ordner).

**Geprüft:** Der Ablauf Prüfen → Bestätigung → Löschen → Ergebnis → Protokoll lief über die Befehle der Oberfläche (Selbsttest `--selftest-clean`) mit einem eigenen TEMP-Ordner als Sandbox: nur alte Dateien weg, junge und der ausgeschlossene Ordner `claude` blieben, Ordner und Protokoll stimmten.

**Nicht geprüft:** ein Löschlauf auf den echten Zielen (Caches, `C:\Windows\Temp`; das braucht deine Entscheidung), Mausklicks auf die Knöpfe (die Befehle dahinter sind geprüft), Windows 10, ob uv-Umgebungen nach dem Leeren des uv-Caches weiterlaufen (Annahme: Hardlinks), die Windows-Ziele ohne Administratorrechte, die neuen Browser- und Entwickler-Ziele auf echten Daten.

## Stand Auswahlkorb, Papierkorb, Entwickler-Funde, Verlauf
Grundlage: der Wunsch, Temp zu löschen und andere Dateien selbst in den Papierkorb zu legen. Neu in `HokiiCleaner.Actions` (`PathGuard`, `RecycleEngine`, `ShellRecycler`, `RecycleBin`, `RecycleLog`), `HokiiCleaner.Core` (`ScanTreeEditor`, `DevArtifactFinder`, `ScanSnapshot`) und der App (`BasketViewModel`, `BinViewModel`, `DevViewModel`, `HistoryViewModel`).

**Auswahlkorb:** Haken in Orte, Typen (größte Dateien), Duplikate und Entwickler-Funde merken Einträge vor; eine Leiste unter allen Ansichten zeigt die Summe, die Liste und die Aktion „In den Papierkorb …“. Ordner und darin liegende Einträge werden nicht doppelt gezählt. Beim Klick wird zuerst **geplant** (nichts verändert): Schutzliste, Verknüpfungen, Papierkorb des Laufwerks und dessen Größengrenze. Was abgelehnt wird, steht mit Grund in der Bestätigung; eine Gruppe identischer Dateien, deren Kopien alle verschoben würden, blockiert die Aktion. Erst danach folgt „In den Papierkorb verschieben“. Jeder Eintrag wird unmittelbar vor dem Verschieben noch einmal geprüft, Fehler einzelner Einträge stoppen die übrigen nicht, nach dem Lauf wird nachgesehen, ob der Eintrag wirklich weg ist. Ergebnis und Protokoll (`logs\papierkorb-*.txt`, mit jedem verschobenen Pfad) bleiben sichtbar; gescheiterte Einträge bleiben für einen neuen Versuch in der Auswahl.
- **Papierkorb-Größengrenze:** Windows löscht Einträge, die größer als der Papierkorb des Laufwerks sind, ohne Rückfrage endgültig. HokiiCleaner liest die Grenze aus der Registry (hier etwa 48,6 GB für C: und D:) und lehnt solche Einträge ab; ebenso Laufwerke ohne Papierkorb (Netzwerk, Wechsel) und die Einstellung „Dateien sofort löschen“. Ist die Grenze unbekannt, fragt zusätzlich Windows nach (`FOF_WANTNUKEWARNING`).
- **Schutzliste (`PathGuard`):** Windows, Program Files, `ProgramData\Microsoft`, Schlüsselordner des Profils (Credentials, Protect, Crypto), der Programmordner von HokiiCleaner und `%LOCALAPPDATA%\HokiiCleaner`, Systemdateien am Laufwerksstamm (`pagefile.sys`, `$Recycle.Bin`, …) sowie Ordner, die solche Orte **enthalten** (z. B. das Benutzerprofil selbst). Profilordner wie Dokumente sind selbst geschützt, ihr Inhalt nicht. Eigene Ordner schützt der Benutzer mit „Ordner schützen“ in Orte; die Liste steht in den Einstellungen und in `settings.json`.
- **OneDrive:** Einträge darin werden mit der Cloud abgeglichen; die Bestätigung sagt das.
- **Duplikate:** Haken je Kopie; „Älteste / Neueste / Kürzesten Pfad behalten“ merkt in jeder Gruppe alle anderen vor. Die letzte unmarkierte Kopie einer Gruppe lässt sich nicht markieren.
- **Scan-Baum:** Nach dem Verschieben werden die Einträge aus dem Baum genommen, die Summen darüber nachgeführt und der Papierkorb-Zweig des Scans neu gelesen (der Papierkorb benennt Einträge selbst um). Die Kategorie „Papierkorb“ wächst, „belegt“ bleibt gleich; Typen, Orte, Sonar, Programme, Duplikate und Entwickler-Funde werden aktualisiert.

**Papierkorb-Ansicht:** Größe und Anzahl je Laufwerk (Windows), Inhalt des eigenen Benutzers aus den `$I`/`$R`-Dateien (ursprünglicher Pfad, Löschzeit, Größe; Ordnergrößen werden nachgemessen), Wiederherstellen am ursprünglichen Ort (überschreibt nichts), Leeren mit eigener Bestätigung, „Im Explorer öffnen“. Verwaiste Protokolleinträge ohne Inhalt (auf diesem Rechner 3) zählt die Ansicht als Hinweis statt als Fehler.

**Entwickler-Funde:** erkennt aus dem Scan, ohne Dateizugriff: `node_modules` neben `package.json`, Python-Umgebungen (`pyvenv.cfg`), `bin`/`obj` neben .NET-Projektdateien, `target` neben `Cargo.toml` oder `pom.xml`, `build` neben `build.gradle(.kts)`, `.next`. Filter „Projekt unverändert seit …“ (Aktivität ohne die erzeugten Ordner). Ordner von Editor-Erweiterungen und Werkzeugen (AppData, `.vscode`, `.cursor`, `.npm`, …) werden nicht durchsucht. Echtlauf `D:\Workspace`: 82 Ordner, 32 GB, davon 4 mit Projekt seit mindestens 90 Tagen unverändert.

**Verlauf:** „Stand speichern“ legt Pfad und Größe aller Ordner ab 50 MB als komprimierte JSON-Datei ab (keine Dateien, keine Inhalte, bleibt lokal). Der Vergleich mit dem aktuellen Scan desselben Ordners zeigt, was gewachsen oder geschrumpft ist, mit der tiefsten Ursache statt jedes Elternordners; fehlt ein Ordner in einem Stand, war er dort unter 50 MB. Export als CSV.

**Weiteres:** „Deinstallieren …“ in der Programmliste startet nach Rückfrage den vom Hersteller hinterlegten Befehl (nur `.exe`-Befehle; auf diesem Rechner werden 120 von 122 erkannt, die zwei `winget uninstall`-Einträge nicht); HokiiCleaner löscht dabei selbst nichts. Der Hinweis zu virtuellen Datenträgern erklärt, wie man WSL- und Docker-Platten verkleinert (HokiiCleaner führt das nicht aus).

**Diagnose:** `--mark <n>`, `--keep oldest|newest|shortest`, `--basket confirm` (verschieben nie etwas); `--view dev|bin|history`; Selbsttests `--selftest-move <Ordner>` (nur Ordner, deren Name mit `hokiicleaner-selftest-` beginnt: über den Auswahlkorb in den echten Papierkorb, prüft Scan-Baum und Papierkorb-Zweig, stellt wieder her), `--selftest-history true`, `--selftest-clean true` (nur mit TEMP-Sandbox); Berichte als `.txt` neben dem Screenshot.

**Tests:** 296 bestanden (vorher 221) (neu u. a. `RecycleTests`, `ScanTreeEditorTests`, `DevAndHistoryTests`, `UninstallCommandTests`, erweiterte `CleanEngineTests`); Papierkorb-Tests laufen mit einem Fake-Recycler und fassen den echten Papierkorb nie an.

**Geprüft am echten System:** Windows-Papierkorb mit Testdateien (Verschieben von Datei und Ordner, Abfrage von Anzahl und Größe, Auflisten mit Pfad und Löschzeit, Wiederherstellen), danach der gesamte Weg über die Befehle der Oberfläche mit einem Testordner auf `D:` und ganzer Laufwerksanalyse: Papierkorb-Zweig des Scans +7,35 MB bei 7,34 MB Ordnergröße, Gesamtsumme des Scans unverändert, Wiederherstellung stellte alles wieder her. Dabei gefunden und behoben: `SHQUERYRBINFO` hat unter 64 Bit eine andere Struktur-Packung als zuerst angenommen (die Abfrage scheiterte mit `E_INVALIDARG`), und PowerShell reicht `$null` an `string`-Parameter als Leerstring weiter (die API behandelt nun leere Strings wie `null`).

**Nicht geprüft:** Mausklicks auf die einzelnen Knöpfe (die Abläufe laufen über dieselben Befehle, aber nicht über Klicks), das Dialogverhalten von Windows, wenn doch einmal ein Eintrag nicht in den Papierkorb passt (`FOF_WANTNUKEWARNING` nie ausgelöst und damit nie gesehen), Pfade über 260 Zeichen im Papierkorb (die Shell-Funktion meldet dann einen Fehler, der als gescheitert erscheint), Wechsel- und Netzlaufwerke, Windows 10, schmale Fenster, mehrere Benutzerkonten.

**Grenzen:** Der Papierkorb zeigt nur die Einträge des angemeldeten Benutzers; die Summe von Windows kann größer sein. Der Verlauf sieht nur Ordner ab 50 MB. Entwickler-Funde erkennen nur die genannten Muster und sagen nichts über Abhängigkeiten außerhalb der Projektdateien. Die Schutzliste ist eine Vorsichtsmaßnahme, kein Ersatz für Sicherungen.

## Offene Punkte
- **.NET-Version:** installiert ist SDK 8.0.426. Meines Wissens endet der Support für .NET 8 im November 2026; .NET 10 wäre die aktuelle LTS. Entscheidung: mit `net8.0-windows` starten oder zuerst .NET 10 SDK installieren.
- **UI-Textsprache:** Deutsch angenommen; falls später Mehrsprachigkeit gewünscht ist, Texte von Anfang an in Ressourcendateien.
- **Design-Richtung:** „Messinstrument“ ist mein Vorschlag, noch nicht von dir bestätigt.
- **Treemap:** eigene Zeichnung oder Bibliothek.
- **Übernahme aus CCleaner:** nur Funktionsideen und Abläufe, kein Code, keine Assets, keine Regeldatenbank.
