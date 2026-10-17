using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.App.ViewModels;

namespace HokiiCleaner.App;

public partial class App : Application
{
    private const string Usage =
        "Aufruf: HokiiCleaner.App.exe [--scan <Pfad> [--view types|places|places-filtered|programs|duplicates|dev|history] [--open <Ordner>] [--screenshot <Datei.png>]]"
        + " oder HokiiCleaner.App.exe --view idle|clean|clean-confirm|slow|slow-expanded|bin|history [--screenshot <Datei.png>]; zusätzlich --theme light|dark|system."
        + " Für die Orte-Ansicht: --search <Text>, --sort size|name|modified, --stale true. Für Duplikate: --min-mb 1|10|100 (startet die Suche)."
        + " Auswahlkorb (verschiebt nie etwas): --mark <Anzahl> merkt die ersten Zeilen der Ansicht vor, --keep oldest|newest|shortest merkt in den Duplikaten alle bis auf eine Kopie vor, --basket confirm öffnet nur die Bestätigung."
        + " Selbsttest: --scan <Laufwerk> --selftest-move <Ordner> --screenshot <Datei.png> verschiebt genau diesen Ordner in den Papierkorb, prüft den Scan-Baum und stellt ihn wieder her; der Ordnername muss mit hokiicleaner-selftest- beginnen.";

    private static readonly string[] Views = { "idle", "clean", "clean-confirm", "sonar","types", "places", "places-filtered", "programs", "duplicates", "dev", "bin", "history", "slow", "slow-expanded" };

    private sealed record StartupOptions(
        string? ScanPath, string? ScreenshotPath, string? View, string? OpenPath, ThemeMode? Theme,
        string? Search, PlacesSort? Sort, bool Stale, long? MinMegabytes, int? Mark, string? Keep, bool BasketConfirm, string? SelfTestFolder, bool SelfTestHistory, bool SelfTestClean);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF formatiert Bindings sonst immer in en-US; Zahlen und Prozent sollen der Systemeinstellung folgen.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        StartupOptions options;
        try
        {
            options = ParseArguments(e.Args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "HokiiCleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        ThemeService.Initialize(options.Theme);

        var viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        if (options.View is "slow" or "slow-expanded")
        {
            _ = RunSlowDiagnosticAsync(viewModel, window, options);
            return;
        }

        if (options.View == "idle")
        {
            _ = RunIdleDiagnosticAsync(window, options);
            return;
        }

        if (options.SelfTestClean)
        {
            _ = RunCleanSelfTestAsync(viewModel, window, options);
            return;
        }

        if (options.View == "bin" || (options.View == "history" && options.ScanPath is null))
        {
            _ = RunStandaloneDiagnosticAsync(viewModel, window, options);
            return;
        }

        if (options.View is "clean" or "clean-confirm")
        {
            _ = RunCleanDiagnosticAsync(viewModel, window, options);
            return;
        }

        if (options.SelfTestFolder is not null || options.SelfTestHistory)
        {
            viewModel.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.State) && viewModel.State is ScanState.Ready or ScanState.Failed)
                {
                    await (options.SelfTestFolder is not null
                        ? RunMoveSelfTestAsync(viewModel, window, options)
                        : RunHistorySelfTestAsync(viewModel, window, options));
                }
            };
            _ = viewModel.StartScanAsync(options.ScanPath);
            return;
        }

        if (options.View is not null || options.OpenPath is not null || options.ScreenshotPath is not null)
        {
            viewModel.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName != nameof(MainViewModel.State)
                    || viewModel.State is not (ScanState.Ready or ScanState.Failed))
                {
                    return;
                }

                if (viewModel.State == ScanState.Ready)
                {
                    ApplyDiagnosticView(viewModel, options);
                }

                if (viewModel.State == ScanState.Ready)
                {
                    await WaitUntilPlacesIdleAsync(viewModel);
                    await ApplyBasketDiagnosticAsync(viewModel, options);
                }

                if (options.ScreenshotPath is not null)
                {
                    await WaitUntilPlacesIdleAsync(viewModel);
                    await Task.Delay(1400); // Layout, Bindings und die Aufbau-Animation des Sonar abwarten
                    SaveScreenshot(window, options.ScreenshotPath);
                    Shutdown(viewModel.State == ScanState.Ready ? 0 : 1);
                }
            };
        }

        if (options.ScanPath is not null)
        {
            _ = viewModel.StartScanAsync(options.ScanPath);
        }
    }

    private static StartupOptions ParseArguments(string[] args)
    {
        string? scanPath = null;
        string? screenshotPath = null;
        string? view = null;
        string? openPath = null;
        ThemeMode? theme = null;
        string? search = null;
        PlacesSort? sort = null;
        bool stale = false;
        long? minMegabytes = null;
        int? mark = null;
        string? keep = null;
        bool basketConfirm = false;
        string? selfTestFolder = null;
        bool selfTestHistory = false;
        bool selfTestClean = false;

        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException(Usage);
            }

            switch (args[i])
            {
                case "--scan":
                    scanPath = args[i + 1];
                    break;
                case "--screenshot":
                    screenshotPath = args[i + 1];
                    break;
                case "--view":
                    view = args[i + 1];
                    if (!Views.Contains(view))
                    {
                        throw new ArgumentException($"Unbekannte Ansicht '{view}'. " + Usage);
                    }

                    break;
                case "--open":
                    openPath = args[i + 1];
                    break;
                case "--search":
                    search = args[i + 1];
                    break;
                case "--sort":
                    sort = args[i + 1] switch
                    {
                        "size" => PlacesSort.Size,
                        "name" => PlacesSort.Name,
                        "modified" => PlacesSort.Modified,
                        _ => throw new ArgumentException($"Unbekannte Sortierung '{args[i + 1]}'. " + Usage),
                    };
                    break;
                case "--stale":
                    stale = args[i + 1] == "true";
                    break;
                case "--min-mb":
                    minMegabytes = long.TryParse(args[i + 1], out long mb) && mb is 1 or 10 or 100
                        ? mb
                        : throw new ArgumentException($"--min-mb erwartet 1, 10 oder 100, nicht '{args[i + 1]}'. " + Usage);
                    break;
                case "--mark":
                    mark = int.TryParse(args[i + 1], out int markCount) && markCount > 0
                        ? markCount
                        : throw new ArgumentException($"--mark erwartet eine positive Zahl, nicht '{args[i + 1]}'. " + Usage);
                    break;
                case "--keep":
                    keep = args[i + 1] is "oldest" or "newest" or "shortest"
                        ? args[i + 1]
                        : throw new ArgumentException($"--keep erwartet oldest, newest oder shortest, nicht '{args[i + 1]}'. " + Usage);
                    break;
                case "--basket":
                    basketConfirm = args[i + 1] == "confirm"
                        ? true
                        : throw new ArgumentException($"--basket erwartet confirm, nicht '{args[i + 1]}'. " + Usage);
                    break;
                case "--selftest-move":
                    selfTestFolder = args[i + 1];
                    break;
                case "--selftest-history":
                    selfTestHistory = args[i + 1] == "true";
                    break;
                case "--selftest-clean":
                    selfTestClean = args[i + 1] == "true";
                    break;
                case "--theme":
                    theme = args[i + 1] switch
                    {
                        "light" => ThemeMode.Light,
                        "dark" => ThemeMode.Dark,
                        "system" => ThemeMode.System,
                        _ => throw new ArgumentException($"Unbekanntes Thema '{args[i + 1]}'. " + Usage),
                    };
                    break;
                default:
                    throw new ArgumentException(Usage);
            }
        }

        bool slow = view is "slow" or "slow-expanded";
        bool noScan = slow || view is "idle" or "clean" or "clean-confirm" or "bin" || (view == "history" && scanPath is null) || selfTestClean;
        if (noScan && (scanPath is not null || openPath is not null))
        {
            throw new ArgumentException("Die Ansichten slow, idle und clean brauchen weder --scan noch --open. " + Usage);
        }

        if (!noScan &&(screenshotPath is not null || view is not null || openPath is not null) && scanPath is null)
        {
            throw new ArgumentException("--screenshot, --view und --open brauchen --scan. " + Usage);
        }

        if (selfTestClean && screenshotPath is null)
        {
            throw new ArgumentException("--selftest-clean braucht --screenshot (der Bericht liegt daneben als .txt). " + Usage);
        }

        if ((selfTestFolder is not null || selfTestHistory) && (scanPath is null || screenshotPath is null))
        {
            throw new ArgumentException("--selftest-move braucht --scan und --screenshot (der Bericht liegt daneben als .txt). " + Usage);
        }

        return new StartupOptions(scanPath, screenshotPath, view, openPath, theme, search, sort, stale, minMegabytes, mark, keep, basketConfirm, selfTestFolder, selfTestHistory, selfTestClean);
    }

    /// <summary>Diagnose: startet direkt im Langsam-Check, führt ihn aus und speichert auf Wunsch einen Screenshot.</summary>
    private static async Task RunSlowDiagnosticAsync(MainViewModel viewModel, Window window, StartupOptions options)
    {
        viewModel.Section = Section.Slowness;
        await viewModel.Slow.RunAsync();
        if (options.View == "slow-expanded")
        {
            viewModel.Slow.ExpandFirstFindings(2);
        }

        if (options.ScreenshotPath is null)
        {
            return;
        }

        await Task.Delay(700);
        SaveScreenshot(window, options.ScreenshotPath);
        Current.Shutdown(viewModel.Slow.State == SlownessState.Ready ? 0 : 1);
    }

    /// <summary>Diagnose: Startzustand vor dem ersten Scan (Laufwerksring), auf Wunsch als Screenshot.</summary>
    private static async Task RunIdleDiagnosticAsync(Window window, StartupOptions options)
    {
        if (options.ScreenshotPath is null)
        {
            return;
        }

        await Task.Delay(1400);
        SaveScreenshot(window, options.ScreenshotPath);
        Current.Shutdown(0);
    }

    /// <summary>Diagnose: öffnet den Bereich Aufräumen und prüft (nur lesend; löscht nie), auf Wunsch mit Screenshot.</summary>
    private static async Task RunCleanDiagnosticAsync(MainViewModel viewModel, Window window, StartupOptions options)
    {
        viewModel.Section = Section.Clean; // startet die Prüfung selbst
        DateTime deadline = DateTime.UtcNow.AddMinutes(5);
        while (viewModel.Clean.IsBusy)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Die Prüfung wurde nicht rechtzeitig fertig.");
            }

            await Task.Delay(100);
        }

        if (options.View == "clean-confirm")
        {
            viewModel.Clean.RequestCleanCommand.Execute(null); // nur die Bestätigungsleiste; gelöscht wird nie ohne Klick
        }

        if (options.ScreenshotPath is null)
        {
            return;
        }

        await Task.Delay(700);
        SaveScreenshot(window, options.ScreenshotPath);
        CleanPhase expected = options.View == "clean-confirm" ? CleanPhase.Confirming : CleanPhase.Ready;
        Current.Shutdown(viewModel.Clean.Phase == expected ? 0 : 1);
    }

    /// <summary>Diagnose: stellt nach dem Scan Ansicht, Filter und Ordner so ein, wie die Startparameter es verlangen.</summary>
    private static void ApplyDiagnosticView(MainViewModel viewModel, StartupOptions options)
    {
        if (options.View == "types")
        {
            viewModel.ViewMode = ViewMode.Types;
        }

        if (options.View is "places" or "places-filtered")
        {
            viewModel.ViewMode = ViewMode.Places;
            viewModel.Places.FilterByType = options.View == "places-filtered";
        }

        if (options.OpenPath is not null)
        {
            viewModel.Places.NavigateToPath(options.OpenPath);
        }

        if (options.Sort is PlacesSort sort)
        {
            viewModel.Places.Sort = sort;
        }

        viewModel.Places.OnlyStale = options.Stale;
        if (options.Search is not null)
        {
            viewModel.ViewMode = ViewMode.Places;
            viewModel.Places.SearchText = options.Search;
        }

        if (options.View == "programs")
        {
            viewModel.ViewMode = ViewMode.Programs;
        }

        if (options.View == "duplicates")
        {
            viewModel.ViewMode = ViewMode.Duplicates;
            if (options.MinMegabytes is long minMegabytes)
            {
                viewModel.Duplicates.MinMegabytes = minMegabytes;
                _ = viewModel.Duplicates.StartAsync();
            }
        }

        if (options.View == "dev")
        {
            viewModel.ViewMode = ViewMode.Dev;
        }

        if (options.View == "history")
        {
            viewModel.ViewMode = ViewMode.History;
        }
    }

    /// <summary>
    /// Diagnose: merkt Zeilen für den Auswahlkorb vor und öffnet höchstens die Bestätigung. Verschoben wird nie etwas;
    /// der Bestätigungsknopf wird nicht betätigt.
    /// </summary>
    private static async Task ApplyBasketDiagnosticAsync(MainViewModel viewModel, StartupOptions options)
    {
        if (options.View == "duplicates" && options.MinMegabytes is not null)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(10);
            while (viewModel.Duplicates.IsRunning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }

        if (options.Keep is not null)
        {
            ICommand command = options.Keep switch
            {
                "oldest" => viewModel.Duplicates.KeepOldestCommand,
                "newest" => viewModel.Duplicates.KeepNewestCommand,
                _ => viewModel.Duplicates.KeepShortestCommand,
            };
            command.Execute(null);
        }

        if (options.Mark is int count)
        {
            switch (viewModel.ViewMode)
            {
                case ViewMode.Places:
                    foreach (FolderRowViewModel row in viewModel.Places.Rows.Where(r => r.CanMark).Take(count))
                    {
                        row.IsMarked = true;
                    }

                    break;
                case ViewMode.Types:
                    foreach (FileRowViewModel row in viewModel.TopFiles.Where(r => r.CanMark).Take(count))
                    {
                        row.IsMarked = true;
                    }

                    break;
                case ViewMode.Dev:
                    foreach (DevRowViewModel row in viewModel.Dev.Rows.Where(r => r.CanMark).Take(count))
                    {
                        row.IsMarked = true;
                    }

                    break;
                case ViewMode.Duplicates:
                    foreach (DuplicateFileViewModel file in viewModel.Duplicates.Files.Where(f => f.CanMark).Take(count))
                    {
                        file.IsMarked = true;
                    }

                    break;
            }
        }

        if (options.BasketConfirm && viewModel.Basket.RequestMoveCommand.CanExecute(null))
        {
            viewModel.Basket.RequestMoveCommand.Execute(null);
            DateTime deadline = DateTime.UtcNow.AddMinutes(1);
            while (viewModel.Basket.Phase == BasketPhase.Planning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// Selbsttest: verschiebt einen eigens angelegten Testordner (Name beginnt mit "hokiicleaner-selftest-") über den Auswahlkorb in
    /// den echten Papierkorb, prüft Scan-Baum und Papierkorb-Zweig und stellt den Ordner wieder her. Fasst nie etwas anderes an.
    /// </summary>
    private static async Task RunMoveSelfTestAsync(MainViewModel viewModel, Window window, StartupOptions options)
    {
        string report = options.ScreenshotPath! + ".txt";
        var lines = new List<string>();
        bool ok = true;

        void Check(string what, bool passed, string? detail = null)
        {
            ok &= passed;
            lines.Add($"{(passed ? "OK    " : "FEHLER")} {what}{(detail is null ? string.Empty : " – " + detail)}");
        }

        async Task WaitAsync(Func<bool> done, string what, int seconds = 180)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Zeitüberschreitung: {what}");
                }

                await Task.Delay(100);
            }
        }

        try
        {
            string folder = System.IO.Path.GetFullPath(options.SelfTestFolder!).TrimEnd('\\');
            if (!System.IO.Path.GetFileName(folder).StartsWith("hokiicleaner-selftest-", StringComparison.Ordinal) || !System.IO.Directory.Exists(folder))
            {
                throw new ArgumentException($"Der Selbsttest verschiebt nur vorhandene Ordner, deren Name mit hokiicleaner-selftest- beginnt: {folder}");
            }

            HokiiCleaner.Core.Scanning.DirectoryNode root = viewModel.Places.CurrentFolder is { } current
                ? current
                : throw new InvalidOperationException("Kein Scan-Ergebnis.");
            while (root.Parent is not null)
            {
                root = root.Parent;
            }

            string driveRoot = System.IO.Path.GetPathRoot(folder)!;
            string binFolder = HokiiCleaner.Actions.RecycleBin.UserFolder(driveRoot);
            HokiiCleaner.Core.Scanning.DirectoryNode? testNode = root.FindDescendant(folder);
            Check("Testordner liegt im Scan", testNode is not null);
            long testBytes = testNode?.TotalSizeOnDisk ?? 0;
            long rootBefore = root.TotalSizeOnDisk;
            long binBefore = root.FindDescendant(binFolder)?.TotalSizeOnDisk ?? 0;
            long binItemsBefore = HokiiCleaner.Actions.RecycleBin.Query(driveRoot).Items;
            lines.Add($"Vorher: Scan {rootBefore:N0} Byte, Testordner {testBytes:N0} Byte, Papierkorb-Zweig {binBefore:N0} Byte, Papierkorb {binItemsBefore} Einträge");

            // Markieren, planen, bestätigen: derselbe Weg wie per Klick.
            Check("Markieren nimmt den Ordner auf", viewModel.Basket.Add(new BasketEntry(folder, true, testBytes, testNode?.TotalFileCount ?? 0, "Selbsttest")));
            viewModel.Basket.RequestMoveCommand.Execute(null);
            await WaitAsync(() => viewModel.Basket.Phase is BasketPhase.Confirming or BasketPhase.Collecting, "Planung");
            Check("Planung bietet die Bestätigung an", viewModel.Basket.Phase == BasketPhase.Confirming, viewModel.Basket.BlockedText);
            if (viewModel.Basket.Phase == BasketPhase.Confirming)
            {
                lines.Add("Bestätigungstext: " + viewModel.Basket.ConfirmTitle);
                viewModel.Basket.ConfirmCommand.Execute(null);
                await WaitAsync(() => viewModel.Basket.Phase == BasketPhase.Collecting && viewModel.Basket.HasResult, "Verschieben");
            }

            lines.Add("Ergebnis: " + viewModel.Basket.ResultTitle + " | " + viewModel.Basket.ResultNote);
            await Task.Delay(1500); // Scan-Baum und Ansichten nachführen lassen
            await WaitUntilPlacesIdleAsync(viewModel);

            Check("Ordner ist von seinem Ort verschwunden", !System.IO.Directory.Exists(folder));
            Check("Auswahlkorb ist leer", viewModel.Basket.Count == 0);
            Check("Ordner nicht mehr im Scan-Baum", root.FindDescendant(folder) is null);
            long binItemsAfter = HokiiCleaner.Actions.RecycleBin.Query(driveRoot).Items;
            Check("Papierkorb hat einen Eintrag mehr", binItemsAfter == binItemsBefore + 1, $"{binItemsBefore} → {binItemsAfter}");
            long binAfter = root.FindDescendant(binFolder)?.TotalSizeOnDisk ?? 0;
            long rootAfter = root.TotalSizeOnDisk;
            lines.Add($"Nachher: Scan {rootAfter:N0} Byte, Papierkorb-Zweig {binAfter:N0} Byte");
            Check("Papierkorb-Zweig im Scan wuchs um die Größe des Ordners", Math.Abs((binAfter - binBefore) - testBytes) <= Math.Max(8192, testBytes / 50), $"+{binAfter - binBefore:N0} statt {testBytes:N0}");
            Check("Gesamtsumme des Scans blieb (Platz belegt, nur umgezogen)", Math.Abs(rootAfter - rootBefore) <= Math.Max(65536, testBytes / 20), $"Differenz {rootAfter - rootBefore:N0}");

            // Papierkorb-Ansicht zeigen und wiederherstellen.
            viewModel.ViewMode = ViewMode.Bin;
            await Task.Delay(500);
            await viewModel.Bin.RefreshAsync();
            BinRowViewModel? row = viewModel.Bin.Rows.FirstOrDefault(r => string.Equals(r.Entry.OriginalPath.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase));
            Check("Papierkorb-Ansicht listet den Ordner", row is not null);
            await Task.Delay(1000);
            SaveScreenshot(window, options.ScreenshotPath!);
            if (row is not null)
            {
                viewModel.Bin.RestoreCommand.Execute(row);
                await WaitUntilAsync(() => System.IO.Directory.Exists(folder), "Wiederherstellen");
                await WaitAsync(() => viewModel.Bin.Phase == BinPhase.Idle, "Papierkorb lesen");
                Check("Ordner ist wiederhergestellt", System.IO.Directory.Exists(folder));
                await Task.Delay(1500);
                long binRestored = root.FindDescendant(binFolder)?.TotalSizeOnDisk ?? 0;
                Check("Papierkorb-Zweig im Scan ist wieder wie vorher", Math.Abs(binRestored - binBefore) <= 8192, $"{binRestored:N0} statt {binBefore:N0}");
                Check("Papierkorb ist wieder wie vorher", HokiiCleaner.Actions.RecycleBin.Query(driveRoot).Items == binItemsBefore);
            }
        }
        catch (Exception ex)
        {
            ok = false;
            lines.Add("FEHLER Selbsttest abgebrochen: " + ex);
        }

        lines.Add(ok ? "ERGEBNIS: bestanden" : "ERGEBNIS: NICHT bestanden");
        System.IO.File.WriteAllLines(report, lines);
        Current.Shutdown(ok ? 0 : 1);
    }

    /// <summary>
    /// Selbsttest: speichert den aktuellen Scan als Stand, vergleicht ihn mit dem aktuellen Scan und löscht den eben gespeicherten
    /// Stand wieder. Berührt keinen anderen gespeicherten Stand.
    /// </summary>
    private static async Task RunHistorySelfTestAsync(MainViewModel viewModel, Window window, StartupOptions options)
    {
        string report = options.ScreenshotPath! + ".txt";
        var lines = new List<string>();
        bool ok = true;

        void Check(string what, bool passed, string? detail = null)
        {
            ok &= passed;
            lines.Add($"{(passed ? "OK    " : "FEHLER")} {what}{(detail is null ? string.Empty : " – " + detail)}");
        }

        try
        {
            HistoryViewModel history = viewModel.History;
            viewModel.ViewMode = ViewMode.History;
            await Task.Delay(500);
            HashSet<string> before = history.Items.Select(i => i.Info.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lines.Add($"Vorher gespeicherte Stände: {before.Count}");

            history.SaveCommand.Execute(null);
            await WaitUntilAsync(() => !history.IsBusy && history.Items.Count > before.Count, "Speichern");
            HistoryItemViewModel? mine = history.Items.FirstOrDefault(i => !before.Contains(i.Info.FilePath));
            Check("Neuer Stand erscheint in der Liste", mine is not null, history.ErrorText);
            Check("Stand liegt als Datei vor", mine is not null && System.IO.File.Exists(mine.Info.FilePath), mine?.Info.FilePath);
            Check("Stand gehört zum aktuellen Scan", mine?.MatchesCurrent == true);

            if (mine is not null)
            {
                history.SelectedItem = mine;
                history.CompareCommand.Execute(null);
                await WaitUntilAsync(() => !history.IsBusy && (history.HasResult || history.HasError), "Vergleich");
                Check("Vergleich ohne Fehler", !history.HasError, history.ErrorText);
                lines.Add("Ergebnis: " + history.ResultText);
                Check("Gleicher Scan ergibt keine Änderungen", history.Changes.Count == 0);
                await Task.Delay(1000);
                SaveScreenshot(window, options.ScreenshotPath!);

                history.RequestDeleteCommand.Execute(null);
                history.ConfirmDeleteCommand.Execute(null);
                Check("Eigener Stand wurde wieder gelöscht", !System.IO.File.Exists(mine.Info.FilePath));
                Check("Andere Stände sind unberührt", before.All(System.IO.File.Exists));
            }
        }
        catch (Exception ex)
        {
            ok = false;
            lines.Add("FEHLER Selbsttest abgebrochen: " + ex);
        }

        lines.Add(ok ? "ERGEBNIS: bestanden" : "ERGEBNIS: NICHT bestanden");
        System.IO.File.WriteAllLines(report, lines);
        Current.Shutdown(ok ? 0 : 1);
    }

    /// <summary>
    /// Selbsttest für Aufräumen: Der Prozess läuft mit einem eigenen TEMP-Ordner (Sandbox). Der Test wählt nur das Ziel
    /// "Temp-Ordner des Benutzers" und bricht ab, wenn dessen Wurzel nicht in der Sandbox liegt. Windows-Temp und alle
    /// Caches werden nie ausgewählt.
    /// </summary>
    private static async Task RunCleanSelfTestAsync(MainViewModel viewModel, Window window, StartupOptions options)
    {
        string report = options.ScreenshotPath! + ".txt";
        var lines = new List<string>();
        bool ok = true;

        void Check(string what, bool passed, string? detail = null)
        {
            ok &= passed;
            lines.Add($"{(passed ? "OK    " : "FEHLER")} {what}{(detail is null ? string.Empty : " – " + detail)}");
        }

        try
        {
            string sandbox = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\');
            if (!sandbox.Contains("hokiicleaner-e2e-", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Der Selbsttest läuft nur mit einem TEMP-Ordner, dessen Pfad hokiicleaner-e2e- enthält: {sandbox}");
            }

            viewModel.Section = Section.Clean;
            await WaitUntilAsync(() => !viewModel.Clean.IsBusy && viewModel.Clean.Phase == CleanPhase.Ready, "Prüfung");
            CleanTargetRowViewModel[] rows = viewModel.Clean.Groups.SelectMany(g => g.Rows).ToArray();
            CleanTargetRowViewModel temp = rows.Single(r => r.Target.Id == "user-temp");
            Check("Wurzel des Ziels liegt in der Sandbox", temp.Target.Roots.All(r => r.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase)), string.Join("; ", temp.Target.Roots));
            lines.Add($"Gefunden: {temp.Files} Dateien, {temp.Bytes:N0} Byte");
            Check("Nur die alten Dateien zählen (3 von 5)", temp.Files == 3, $"{temp.Files}");

            foreach (CleanTargetRowViewModel row in rows)
            {
                row.IsSelected = row == temp;
            }

            Check("Nur das Sandbox-Ziel ist gewählt", rows.Count(r => r.IsSelected) == 1);
            viewModel.Clean.RequestCleanCommand.Execute(null);
            Check("Bestätigung erscheint, bevor etwas gelöscht wird", viewModel.Clean.Phase == CleanPhase.Confirming);
            Check("Noch nichts gelöscht", System.IO.File.Exists(System.IO.Path.Combine(sandbox, "alt1.tmp")));
            lines.Add("Bestätigungstext: " + viewModel.Clean.ConfirmText);

            viewModel.Clean.ConfirmCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Clean.HasResult && viewModel.Clean.Phase is CleanPhase.Ready or CleanPhase.Idle, "Löschen");
            await WaitUntilAsync(() => !viewModel.Clean.IsBusy, "Nachprüfung");
            lines.Add("Ergebnis: " + viewModel.Clean.ResultTitle + " | " + viewModel.Clean.ResultNote);
            Check("Alte Dateien sind weg", !System.IO.File.Exists(System.IO.Path.Combine(sandbox, "alt1.tmp"))
                && !System.IO.File.Exists(System.IO.Path.Combine(sandbox, "unter", "alt2.tmp"))
                && !System.IO.File.Exists(System.IO.Path.Combine(sandbox, "unter", "tief", "alt3.tmp")));
            Check("Junge Dateien sind geblieben", System.IO.File.Exists(System.IO.Path.Combine(sandbox, "neu1.tmp"))
                && System.IO.File.Exists(System.IO.Path.Combine(sandbox, "unter", "neu2.tmp")));
            Check("Der Temp-Ordner selbst ist geblieben", System.IO.Directory.Exists(sandbox));
            Check("Ausgeschlossener Ordner claude ist unberührt", System.IO.File.Exists(System.IO.Path.Combine(sandbox, "claude", "arbeit.tmp")));
            Check("Protokoll wurde geschrieben", viewModel.Clean.HasLog && System.IO.File.Exists(viewModel.Clean.LogPath!), viewModel.Clean.LogPath);
            await Task.Delay(800);
            SaveScreenshot(window, options.ScreenshotPath!);
        }
        catch (Exception ex)
        {
            ok = false;
            lines.Add("FEHLER Selbsttest abgebrochen: " + ex);
        }

        lines.Add(ok ? "ERGEBNIS: bestanden" : "ERGEBNIS: NICHT bestanden");
        System.IO.File.WriteAllLines(report, lines);
        Current.Shutdown(ok ? 0 : 1);
    }

    private static async Task WaitUntilAsync(Func<bool> done, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Zeitüberschreitung: {what}");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>Diagnose: Papierkorb- oder Verlaufsansicht ohne Scan, auf Wunsch als Screenshot.</summary>
    private static async Task RunStandaloneDiagnosticAsync(MainViewModel viewModel, Window window, StartupOptions options)
    {
        viewModel.ViewMode = options.View == "bin" ? ViewMode.Bin : ViewMode.History;
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        while (viewModel.Bin.IsBusy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (options.ScreenshotPath is null)
        {
            return;
        }

        await Task.Delay(1500);
        SaveScreenshot(window, options.ScreenshotPath);
        Current.Shutdown(0);
    }

    /// <summary>Wartet, bis Orte, Programmliste und Duplikat-Suche fertig sind, bevor ein Screenshot gemacht wird.</summary>
    private static async Task WaitUntilPlacesIdleAsync(MainViewModel viewModel)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(5);
        while (viewModel.Places.IsBusy || viewModel.Programs.IsBusy || viewModel.Duplicates.IsRunning)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Die Ansicht wurde nicht rechtzeitig berechnet.");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>Diagnose: speichert den Fensterinhalt (ohne Titelleiste) als PNG. Funktioniert auch ohne sichtbaren Desktop.</summary>
    private static void SaveScreenshot(Window window, string path)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
