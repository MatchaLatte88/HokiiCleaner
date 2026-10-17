using System.Globalization;
using System.Windows.Media;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.App.Presentation;
using HokiiCleaner.Core;
using HokiiCleaner.Core.Categorization;

namespace HokiiCleaner.App.ViewModels;

public sealed class CategoryRowViewModel : ObservableObject
{
    private bool _isHovered;

    public CategoryRowViewModel(CategoryInfo info, CategoryTotals totals, long totalSizeOnDisk)
    {
        Category = info.Category;
        Name = info.Name;
        Brush = info.Brush;
        SizeOnDisk = totals.SizeOnDisk;
        double share = totalSizeOnDisk == 0 ? 0 : (double)totals.SizeOnDisk / totalSizeOnDisk;
        ShareValue = share * 100;
        ShareText = share.ToString("P1", CultureInfo.CurrentCulture);
        SizeText = ByteFormat.Format(totals.SizeOnDisk);
        FileCountText = $"{totals.FileCount.ToString("N0", CultureInfo.CurrentCulture)} {(totals.FileCount == 1 ? "Datei" : "Dateien")}";
    }

    public FileCategory Category { get; }

    public string Name { get; }

    public Brush Brush { get; }

    public long SizeOnDisk { get; }

    /// <summary>Anteil in Prozent (0..100) für die Anteilsleiste.</summary>
    public double ShareValue { get; }

    public string ShareText { get; }

    public string SizeText { get; }

    public string FileCountText { get; }

    /// <summary>Wird gesetzt, wenn die Kategorie im Messband oder in der Liste überfahren wird.</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set => Set(ref _isHovered, value);
    }
}
