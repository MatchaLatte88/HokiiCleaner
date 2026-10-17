using Cleanii.Core.Diagnostics;

/// <summary>Aufruf: Cleanii.Cli slow [--window SEKUNDEN] [--all]  – gibt den Langsam-Check als Text aus.</summary>
internal static class SlowCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new SlownessOptions();
        bool showAll = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--window":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int seconds) || seconds < 1)
                    {
                        Console.Error.WriteLine("--window erwartet eine positive ganze Zahl (Sekunden).");
                        return 2;
                    }

                    options = options with { SampleWindow = TimeSpan.FromSeconds(seconds) };
                    i++;
                    break;
                case "--all":
                    showAll = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unbekanntes Argument: {args[i]}");
                    return 2;
            }
        }

        var progress = new Progress<string>(message => Console.Error.WriteLine(message));
        SlownessReport report = await SlownessAnalyzer.RunAsync(options, progress);

        Console.WriteLine($"Langsam-Check in {report.Elapsed.TotalSeconds:N1} s, Administrator: {(report.Snapshot.IsElevated ? "ja" : "nein")}");
        Console.WriteLine($"Befunde: {report.Checks.Count(c => c.Outcome == CheckOutcome.Critical)} kritisch, "
            + $"{report.Checks.Count(c => c.Outcome == CheckOutcome.Warning)} Warnung, "
            + $"{report.Checks.Count(c => c.Outcome == CheckOutcome.Info)} Hinweis, "
            + $"{report.Checks.Count(c => c.Outcome == CheckOutcome.Unavailable)} nicht messbar, "
            + $"{report.Checks.Count(c => c.Outcome == CheckOutcome.Ok)} unauffällig");

        foreach (Check check in report.Checks.OrderBy(c => c.DisplayRank).ThenBy(c => c.Area).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (check.Outcome == CheckOutcome.Ok && !showAll)
            {
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"[{Label(check.Outcome)}] {check.Title}  ({check.Area})");
            Console.WriteLine($"    {check.Measurement}");
            if (check.Threshold is not null)
            {
                Console.WriteLine($"    Schwelle: {check.Threshold}");
            }

            Console.WriteLine($"    {check.Explanation}");
            Console.WriteLine($"    Quelle: {check.Source}");
            foreach (string detail in check.Details)
            {
                Console.WriteLine($"      - {detail}");
            }
        }

        int okCount = report.Checks.Count(c => c.Outcome == CheckOutcome.Ok);
        if (!showAll && okCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{okCount} unauffällige Prüfungen nicht angezeigt (mit --all alle zeigen).");
        }

        return 0;
    }

    private static string Label(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Critical => "KRITISCH",
        CheckOutcome.Warning => "WARNUNG",
        CheckOutcome.Info => "HINWEIS",
        CheckOutcome.Unavailable => "NICHT GEMESSEN",
        _ => "OK",
    };
}
