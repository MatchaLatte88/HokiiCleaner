using System.Globalization;
using System.IO;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.Core;
using HokiiCleaner.Core.Categorization;

namespace HokiiCleaner.App.ViewModels;

public sealed class FileRowViewModel : ObservableObject
{
    private BasketViewModel? _basket;
    private bool _isMarked;

    public FileRowViewModel(TopFile file)
    {
        Name = file.File.Name;
        DirectoryPath = file.Directory.FullPath;
        FullPath = Path.Combine(DirectoryPath, Name);
        SizeBytes = file.File.SizeOnDisk;
        LogicalBytes = file.File.Length;
        SizeText = ByteFormat.Format(file.File.SizeOnDisk);
        ModifiedUtc = file.File.LastWriteUtc;
        ModifiedText = ModifiedUtc == DateTime.MinValue
            ? string.Empty
            : ModifiedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public string FullPath { get; }

    public long SizeBytes { get; }

    public long LogicalBytes { get; }

    public string SizeText { get; }

    public DateTime ModifiedUtc { get; }

    /// <summary>Datum der letzten Änderung; leer, wenn unbekannt.</summary>
    public string ModifiedText { get; }

    /// <summary>Verbindet die Zeile mit dem Auswahlkorb (Haken links).</summary>
    public void AttachBasket(BasketViewModel basket)
    {
        _basket = basket;
        string? why = basket.WhyNotMarkable(FullPath);
        CanMark = why is null;
        MarkToolTip = why ?? "Für den Papierkorb vormerken (verschoben wird erst nach Bestätigung)";
        _isMarked = CanMark && basket.Contains(FullPath);
    }

    public bool CanMark { get; private set; }

    public string MarkToolTip { get; private set; } = string.Empty;

    /// <summary>Für den Papierkorb vorgemerkt. Schlägt das Vormerken fehl (z. B. geschützt), springt der Haken zurück.</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_basket is null || !CanMark || value == _isMarked)
            {
                Raise();
                return;
            }

            bool inBasket = value ? _basket.Add(new BasketEntry(FullPath, false, SizeBytes, 1, "Typen")) : !_basket.Remove(FullPath);
            _isMarked = inBasket;
            Raise();
        }
    }

    /// <summary>Gleicht den Haken mit dem Korb ab, wenn sich die Auswahl woanders geändert hat.</summary>
    public void RefreshMark()
    {
        bool marked = CanMark && _basket is not null && _basket.Contains(FullPath);
        if (marked != _isMarked)
        {
            _isMarked = marked;
            Raise(nameof(IsMarked));
        }
    }
}
