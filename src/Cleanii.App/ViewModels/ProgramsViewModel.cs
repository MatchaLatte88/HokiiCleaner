using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Input;
using Cleanii.App.Infrastructure;
using Cleanii.Core;
using Cleanii.Core.Programs;
using Cleanii.Core.Scanning;

namespace Cleanii.App.ViewModels;

/// <summary>Eine Zeile der Programmliste.</summary>
public sealed class ProgramRowViewModel
{
    public ProgramRowViewModel(ProgramSize size, long maxBytes)
    {
        Size = size;
        InstalledProgram program = size.Program;
        Name = program.Name;

        long? bytes = size.DisplayBytes;
        SizeText = bytes is long b ? (size.IsMeasured ? ByteFormat.Format(b) : "≈ " + ByteFormat.Format(b)) : "–";
        SizeNote = size.IsMeasured
            ? "gemessen im Scan"
            : bytes is null ? "Größe unbekannt" : "Angabe des Installers, nicht gemessen";
        ShareValue = bytes is long value && maxBytes > 0 ? 100.0 * value / maxBytes : 0;

        string? date = program.InstallDate is DateTime d ? d.ToString("d", CultureInfo.CurrentCulture) : null;
        DetailText = string.Join(" · ", new[]
        {
            program.Publisher,
            program.Version,
            date is null ? null : $"installiert {date}",
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (DetailText.Length == 0)
        {
            DetailText = "keine weiteren Angaben";
        }

        LocationText = program.InstallLocation is null
            ? "Installationsordner unbekannt"
            : program.LocationInferred ? program.InstallLocation + " (aus dem Symbolpfad abgeleitet)" : program.InstallLocation;
    }

    public ProgramSize Size { get; }

    public string Name { get; }

    public string SizeText { get; }

    public string SizeNote { get; }

    public bool IsMeasured => Size.IsMeasured;

    /// <summary>Anteil an der größten Zeile (0..100) für die Leiste.</summary>
    public double ShareValue { get; }

    public string DetailText { get; }

    public string LocationText { get; }

    public string? InstallLocation => Size.Program.InstallLocation;

    public string Source => Size.Program.Source;

    /// <summary>Der Befehl, der zum Deinstallieren gestartet würde; <c>null</c>, wenn keiner vorliegt oder er nicht erkannt wird.</summary>
    public string? UninstallText => UninstallCommand.TryParse(Size.Program.UninstallString, out string file, out string arguments)
        ? (arguments.Length == 0 ? file : $"{file} {arguments}")
        : null;

    public bool CanUninstall => UninstallText is not null;

    public DirectoryNode? Folder => Size.Folder;
}

/// <summary>
/// Installierte Programme mit der Größe, die der Scan gemessen hat. Rein lesend: Die Liste kommt aus der Registry,
/// es wird nichts deinstalliert oder verändert.
/// </summary>
public sealed class ProgramsViewModel : ObservableObject
{
    private readonly Action<DirectoryNode> _showInPlaces;
    private int _version;
    private bool _isBusy;
    private string _summaryText = string.Empty;
    private string? _noteText;
    private string _errorText = string.Empty;
    private IReadOnlyList<ProgramRowViewModel> _rows = Array.Empty<ProgramRowViewModel>();
    private ProgramRowViewModel? _selectedRow;
    private bool _isConfirmingUninstall;

    public ProgramsViewModel(Action<DirectoryNode> showInPlaces)
    {
        _showInPlaces = showInPlaces;
        ShowInPlacesCommand = new RelayCommand(() => _showInPlaces(SelectedRow!.Folder!), () => SelectedRow?.Folder is not null);
        RevealCommand = new RelayCommand(Reveal, () => SelectedRow?.InstallLocation is not null);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedRow?.InstallLocation is not null);
        ExportCommand = new RelayCommand(Export, () => Rows.Count > 0);
        RequestUninstallCommand = new RelayCommand(() => IsConfirmingUninstall = true, () => SelectedRow?.CanUninstall == true && !IsConfirmingUninstall);
        ConfirmUninstallCommand = new RelayCommand(StartUninstaller, () => IsConfirmingUninstall && SelectedRow?.CanUninstall == true);
        CancelUninstallCommand = new RelayCommand(() => IsConfirmingUninstall = false, () => IsConfirmingUninstall);
        OpenAppsSettingsCommand = new RelayCommand(OpenAppsSettings);
    }

    /// <summary>Meldung für die Statuszeile.</summary>
    public Action<string>? Notify { get; set; }

    public ICommand ShowInPlacesCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand ExportCommand { get; }

    /// <summary>Fragt vor dem Start des Deinstallers nach.</summary>
    public ICommand RequestUninstallCommand { get; }

    /// <summary>Startet den Deinstaller des Herstellers. Cleanii löscht dabei selbst nichts.</summary>
    public ICommand ConfirmUninstallCommand { get; }

    public ICommand CancelUninstallCommand { get; }

    /// <summary>Öffnet "Apps &amp; Features" in den Windows-Einstellungen.</summary>
    public ICommand OpenAppsSettingsCommand { get; }

    public bool IsConfirmingUninstall
    {
        get => _isConfirmingUninstall;
        private set
        {
            if (Set(ref _isConfirmingUninstall, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>Hinweis auf Grenzen der Größenangaben; <c>null</c> = keiner.</summary>
    public string? NoteText
    {
        get => _noteText;
        private set
        {
            if (Set(ref _noteText, value))
            {
                Raise(nameof(HasNote));
            }
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(NoteText);

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorText.Length > 0;

    public IReadOnlyList<ProgramRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (Set(ref _rows, value))
            {
                Raise(nameof(HasRows));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    public ProgramRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                IsConfirmingUninstall = false;
                Raise(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedRow is not null;

    public void Clear()
    {
        _version++;
        IsBusy = false;
        ErrorText = string.Empty;
        SummaryText = string.Empty;
        NoteText = null;
        SelectedRow = null;
        Rows = Array.Empty<ProgramRowViewModel>();
    }

    /// <summary>Liest die Programmliste und ordnet ihr die Größen aus dem Scan zu.</summary>
    public async Task LoadAsync(DirectoryNode scanRoot)
    {
        int version = ++_version;
        IsBusy = true;
        ErrorText = string.Empty;
        SummaryText = "Lese Programmliste …";
        NoteText = null;

        IReadOnlyList<ProgramSize> sizes;
        try
        {
            sizes = await Task.Run(() => InstalledProgramsProbe.Measure(InstalledProgramsProbe.Read(), scanRoot));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            if (version == _version)
            {
                ErrorText = $"Die Programmliste konnte nicht gelesen werden: {ex.Message}";
                SummaryText = string.Empty;
                IsBusy = false;
            }

            return;
        }

        if (version != _version)
        {
            return; // ein neuer Scan hat übernommen
        }

        long maxBytes = sizes.Count == 0 ? 0 : sizes.Max(s => s.DisplayBytes ?? 0);
        Rows = sizes.Select(s => new ProgramRowViewModel(s, maxBytes)).ToArray();
        SelectedRow = Rows.FirstOrDefault();

        int measured = sizes.Count(s => s.IsMeasured);
        long measuredTotal = sizes.Where(s => s.IsMeasured && s.Folder is not null)
            .GroupBy(s => s.Folder)
            .Sum(g => g.Key!.TotalSizeOnDisk);
        SummaryText = string.Create(
            CultureInfo.CurrentCulture,
            $"{sizes.Count:N0} Programme · {measured:N0} mit gemessener Größe, zusammen {ByteFormat.Format(measuredTotal)}");
        var notes = new List<string>();
        if (measured == 0)
        {
            notes.Add("Kein Installationsordner liegt im gescannten Bereich. Für gemessene Größen das ganze Laufwerk analysieren; bis dahin gilt, soweit vorhanden, die Angabe des Installers (≈).");
        }
        else if (measured < sizes.Count)
        {
            notes.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{sizes.Count - measured:N0} Programme liegen außerhalb des gescannten Bereichs oder haben keinen bekannten Ordner. Für sie gilt, soweit vorhanden, die Angabe des Installers (≈); sie ist oft ungenau."));
        }

        if (sizes.Where(s => s.Folder is not null).GroupBy(s => s.Folder).Any(g => g.Count() > 1))
        {
            notes.Add("Programme, die sich einen Ordner teilen (z. B. Sprachpakete), zeigen dessen gesamte Größe; in der Summe zählt der Ordner einmal.");
        }

        NoteText = notes.Count == 0 ? null : string.Join(" ", notes);
        IsBusy = false;
    }

    private void StartUninstaller()
    {
        ProgramRowViewModel? row = SelectedRow;
        IsConfirmingUninstall = false;
        if (row is null || !UninstallCommand.TryParse(row.Size.Program.UninstallString, out string file, out string arguments))
        {
            return;
        }

        // Ein Name ohne Pfad (z. B. MsiExec.exe) wird von Windows über PATH gefunden; nur vollständige Pfade lassen sich vorab prüfen.
        if (Path.IsPathFullyQualified(file) && !File.Exists(file))
        {
            Notify?.Invoke($"Der Deinstaller wurde nicht gefunden: {file}. Das Programm lässt sich über \"Apps & Features\" in den Windows-Einstellungen entfernen.");
            return;
        }

        try
        {
            // ShellExecute, damit Windows bei Bedarf selbst nach Administratorrechten fragt.
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            Notify?.Invoke($"Der Deinstaller von „{row.Name}“ wurde gestartet. Danach die Analyse wiederholen, um die Größen zu aktualisieren.");
        }
        catch (Win32Exception ex)
        {
            Notify?.Invoke($"Der Deinstaller ließ sich nicht starten: {ex.Message}");
        }
    }

    private void OpenAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Notify?.Invoke($"Die Windows-Einstellungen ließen sich nicht öffnen: {ex.Message}");
        }
    }

    private void Reveal()
    {
        try
        {
            ExplorerLauncher.Reveal(SelectedRow!.InstallLocation!);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Notify?.Invoke($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(SelectedRow!.InstallLocation!);
        }
        catch (ExternalException ex)
        {
            Notify?.Invoke($"Der Pfad konnte nicht kopiert werden: {ex.Message}");
        }
    }

    private void Export()
    {
        string[] header = { "Programm", "Herausgeber", "Version", "Installiert am", "Größe (Byte)", "Größe gemessen", "Installationsordner", "Ordner abgeleitet", "Quelle" };
        IEnumerable<IReadOnlyList<string>> lines = Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            r.Size.Program.Name,
            r.Size.Program.Publisher ?? string.Empty,
            r.Size.Program.Version ?? string.Empty,
            r.Size.Program.InstallDate is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
            r.Size.DisplayBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            r.Size.IsMeasured ? "ja" : "nein",
            r.Size.Program.InstallLocation ?? string.Empty,
            r.Size.Program.LocationInferred ? "ja" : "nein",
            r.Source,
        });

        string? message = ExportService.SaveCsv("Programmliste exportieren", ExportService.SuggestName("Programme", ".csv"), header, lines);
        if (message is not null)
        {
            Notify?.Invoke(message);
        }
    }
}
