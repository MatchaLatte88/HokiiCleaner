using System.Windows.Media;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.App.ViewModels;

/// <summary>Ein Glied des Pfads über der Ordnerliste.</summary>
public sealed record CrumbViewModel(string Name, DirectoryNode Node, bool IsLast);

/// <summary>Eine Kategorie in der Zusammensetzung des gewählten Ordners.</summary>
public sealed record CompositionItemViewModel(string Name, Brush Brush, string SizeText, string ShareText);
