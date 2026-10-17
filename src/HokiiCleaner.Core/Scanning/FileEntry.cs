namespace HokiiCleaner.Core.Scanning;

/// <summary>
/// Eine Datei in einem gescannten Ordner. Ordner halten ihre Dateien als Array, nicht als Einzelknoten.
/// </summary>
/// <param name="Name">Dateiname ohne Pfad.</param>
/// <param name="Length">Logische Größe in Byte (wie "Größe" im Explorer).</param>
/// <param name="SizeOnDisk">Belegter Platz in Byte (wie "Größe auf Datenträger"); bei Cloud-Platzhaltern und Links 0.</param>
/// <param name="Attributes">Dateiattribute laut Dateisystem.</param>
/// <param name="HardLinkId">Datei-ID bei geprüften Hardlinks mit mehr als einem Link, sonst 0.</param>
/// <param name="IsHardLinkDuplicate">
/// True, wenn dieselbe Datei (Hardlink) bereits früher in der Baumreihenfolge gezählt wurde.
/// Länge und Belegung bleiben sichtbar, fließen aber nicht in die Summen ein.
/// </param>
/// <param name="LastWriteUtc">
/// Zeitpunkt der letzten Änderung (UTC). Erstellungs- und Zugriffszeit werden nicht gespeichert: Die Zugriffszeit
/// ist auf NTFS oft abgeschaltet und damit unzuverlässig, beide kosten bei Millionen Dateien viel Speicher.
/// </param>
public readonly record struct FileEntry(
    string Name,
    long Length,
    long SizeOnDisk,
    FileAttributes Attributes,
    ulong HardLinkId,
    bool IsHardLinkDuplicate,
    DateTime LastWriteUtc = default);
