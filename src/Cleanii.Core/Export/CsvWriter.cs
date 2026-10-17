using System.Text;

namespace Cleanii.Core.Export;

/// <summary>
/// Schreibt CSV für Excel im deutschen Raum: Semikolon als Trennzeichen, Anführungszeichen nach RFC 4180.
/// Dateinamen stammen vom Datenträger und sind nicht vertrauenswürdig: Zellen, die mit <c>= + - @</c> oder einem
/// Steuerzeichen beginnen, würde Excel als Formel ausführen. Sie bekommen ein <c>'</c> vorangestellt.
/// </summary>
public static class CsvWriter
{
    public const char Separator = ';';

    private static readonly System.Buffers.SearchValues<char> QuotedChars = System.Buffers.SearchValues.Create(new[] { Separator, '"', '\r', '\n' });

    /// <summary>UTF-8 mit Byte-Order-Mark, damit Excel Umlaute richtig liest.</summary>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static void Write(TextWriter writer, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);

        WriteLine(writer, header);
        foreach (IReadOnlyList<string> row in rows)
        {
            if (row.Count != header.Count)
            {
                throw new ArgumentException($"Zeile mit {row.Count} Spalten, erwartet {header.Count}.", nameof(rows));
            }

            WriteLine(writer, row);
        }
    }

    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        bool quote = value.AsSpan().IndexOfAny(QuotedChars) >= 0;
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static void WriteLine(TextWriter writer, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(Separator);
            }

            writer.Write(Escape(cells[i]));
        }

        writer.Write("\r\n");
    }
}
