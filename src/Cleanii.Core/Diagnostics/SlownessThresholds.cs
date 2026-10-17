namespace Cleanii.Core.Diagnostics;

/// <summary>
/// Schwellen der Befund-Regeln. Das sind Startwerte (Faustregeln) und noch nicht an vielen echten Rechnern kalibriert.
/// </summary>
public sealed record SlownessThresholds
{
    public static SlownessThresholds Default { get; } = new();

    public double FreeSpaceWarnPercent { get; init; } = 15;

    public double FreeSpaceCriticalPercent { get; init; } = 10;

    /// <summary>Dauerhafte Auslastung der Platte im Messfenster.</summary>
    public double DiskBusyWarnPercent { get; init; } = 80;

    /// <summary>Durchschnittliche Antwortzeit pro Zugriff; ab hier fühlt sich der Rechner träge an.</summary>
    public double DiskLatencyWarnSeconds { get; init; } = 0.05;

    public double CpuWarnPercent { get; init; } = 70;

    public double CpuCriticalPercent { get; init; } = 90;

    public double MemoryAvailableWarnPercent { get; init; } = 20;

    public double MemoryAvailableCriticalPercent { get; init; } = 10;

    public double CommitWarnPercent { get; init; } = 85;

    public double CommitCriticalPercent { get; init; } = 95;

    /// <summary>Ein Prozess mit dieser Schreib-/Leserate fällt als I/O-Verbraucher auf.</summary>
    public double IoInfoBytesPerSecond { get; init; } = 20 * 1024 * 1024;

    public int StartupInfoCount { get; init; } = 10;

    public int StartupWarnCount { get; init; } = 15;

    public int TasksInfoCount { get; init; } = 10;

    public int ServicesInfoCount { get; init; } = 20;

    public double UptimeInfoDays { get; init; } = 7;

    public double UptimeWarnDays { get; init; } = 14;

    public double DriverAgeInfoDays { get; init; } = 730;

    /// <summary>Anteil der Prozesse, der messbar sein sollte, bevor ein Hinweis auf fehlende Rechte erscheint.</summary>
    public double ProcessCoverageMinimum { get; init; } = 0.9;

    /// <summary>Erweiterungen von Drittanbietern im Explorer-Kontextmenü, ab mehr als dieser Zahl ein Hinweis.</summary>
    public int ShellExtensionsInfoCount { get; init; } = 10;

    /// <summary>Installierte Browser-Erweiterungen insgesamt, ab mehr als dieser Zahl ein Hinweis.</summary>
    public int BrowserExtensionsInfoCount { get; init; } = 15;

    /// <summary>Treiber von Drittanbietern, die älter sind, gelten als alt (nur Hinweis).</summary>
    public double OldDriverInfoDays { get; init; } = 1825;

    /// <summary>Belegung durch Schattenkopien, ab der ein Hinweis erscheint.</summary>
    public long ShadowStorageInfoBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    // Einstufung der Autostart-Programme nach dem, was sie in der Stichprobe verbrauchen.
    public double ImpactMediumCpuPercent { get; init; } = 1;

    public double ImpactHighCpuPercent { get; init; } = 5;

    public double ImpactMediumIoBytesPerSecond { get; init; } = 512 * 1024;

    public double ImpactHighIoBytesPerSecond { get; init; } = 5 * 1024 * 1024;

    public long ImpactMediumMemoryBytes { get; init; } = 150L * 1024 * 1024;

    public long ImpactHighMemoryBytes { get; init; } = 500L * 1024 * 1024;
}
