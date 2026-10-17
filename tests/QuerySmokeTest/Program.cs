using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Community.PowerToys.Run.Plugin.AIHub;
using Wox.Plugin;

internal static class Program
{
    static HashSet<int> AiProcessIds() => Process.GetProcesses()
        .Where(process => process.ProcessName is "node" or "agy" or "gemini" or "claude" or "codex")
        .Select(process => process.Id)
        .ToHashSet();

    [STAThread]
    static void Main(string[] args)
    {
        var before = AiProcessIds();
        var plugin = new Main();

        for (var index = 0; index < 1000; index++)
        {
            var results = plugin.Query(new Query($"@gemini Nur tippen {index} ÄÖÜ", "@"));
            if (results.Count != 1 || results[0].Action is null || results[0].Score < 1_000_000 || results[0].IcoPath != "Images/gemini.png")
            {
                throw new InvalidOperationException("Gültige Eingabe erzeugt kein sichtbares, startbares Ergebnis.");
            }
        }

        var choices = plugin.Query(new Query("@", "@"));
        if (choices.Count != 4 || choices.Any(result => result.Action is not null) || choices[3].IcoPath != "Images/all.png")
        {
            throw new InvalidOperationException("Anbieterauswahl ist unerwartet.");
        }

        foreach (var command in new[] { "@gemini", "@claude", "@codex", "@all", "@ all" })
        {
            var commandResults = plugin.Query(new Query($"{command} Test", "@"));
            if (commandResults.Count != 1 || commandResults[0].Action is null)
            {
                throw new InvalidOperationException($"Befehl {command} wird nicht erkannt.");
            }
        }

        var unmatched = plugin.Query(new Query("@unbekannt", "@"));
        if (unmatched.Count != 0)
        {
            throw new InvalidOperationException("Unbekannter @-Befehl erzeugt Ergebnisse.");
        }

        var inlineResult = plugin.Query(new Query("@ all Noch nicht senden", "@"));
        if (inlineResult.Count != 1 || inlineResult[0].Action is null || inlineResult[0].Title != "An alle drei senden" || inlineResult[0].IcoPath != "Images/all.png")
        {
            throw new InvalidOperationException("Parallelanfrage erzeugt kein Inline-Senden-Ergebnis.");
        }

        Thread.Sleep(250);
        var started = AiProcessIds().Except(before).ToArray();
        if (started.Length != 0)
        {
            throw new InvalidOperationException($"Tippen oder Chatfenster startete unerwartete Prozesse: {string.Join(", ", started)}");
        }

        var longUnbroken = new string('X', 500);
        var chunks = Community.PowerToys.Run.Plugin.AIHub.Main.SplitForDisplay(longUnbroken);
        if (chunks.Count != 6 || chunks.Any(chunk => chunk.Length > 90) || string.Concat(chunks) != longUnbroken)
        {
            throw new InvalidOperationException("Lange Antworten werden nicht verlustfrei in sichtbare Abschnitte geteilt.");
        }

        Console.WriteLine("Query-Smoke-Test bestanden: 1000 Eingaben, Inline-Senden, Aufteilung langer Antworten, 0 Modellprozesse vor Enter.");

        if (args.Contains("--live"))
        {
            RunLiveInlineTest();
            RunLiveExpandTest();
        }
    }

    static void RunLiveInlineTest()
    {
        NoOpApiProxy.ForcedRequeries = 0;
        var plugin = new Main();
        plugin.Init(new PluginInitContext { API = DispatchProxy.Create<IPublicAPI, NoOpApiProxy>() });
        var query = new Query("@ all Antworte exakt mit dem Namen deines Anbieters", "@");
        var send = plugin.Query(query).Single();
        if (send.Action is null || send.Action(null!)) throw new InvalidOperationException("Inline-Senden konnte nicht gestartet werden.");

        var deadline = DateTime.UtcNow.AddSeconds(45);
        List<Result> results;
        do
        {
            Thread.Sleep(250);
            results = plugin.Query(query);
        }
        while (DateTime.UtcNow < deadline && results.Any(result => result.Title.Contains("läuft", StringComparison.OrdinalIgnoreCase)));

        if (results.Count != 4 || results.Take(3).Any(result => result.Title.Contains("Fehler", StringComparison.OrdinalIgnoreCase) || result.Title.Contains("läuft", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Live-Paralleltest lieferte nicht drei erfolgreiche Inline-Antworten: " + string.Join(" | ", results.Select(result => result.Title)));
        }
        var expectedIcons = new[] { "Images/gemini.png", "Images/claude.png", "Images/codex.png" };
        if (!results.Take(3).Select(result => result.IcoPath).SequenceEqual(expectedIcons))
        {
            throw new InvalidOperationException("Anbieterantworten verwenden nicht die erwarteten Logos.");
        }
        if (NoOpApiProxy.ForcedRequeries == 0)
        {
            throw new InvalidOperationException("PowerToys wurde nach dem Senden nicht zur Neuabfrage gezwungen.");
        }
        Console.WriteLine("Live-Inline-Test bestanden: drei Antworten plus gemeinsame Rückfrage direkt als PowerToys-Ergebnisse.");
    }

    static void RunLiveExpandTest()
    {
        NoOpApiProxy.ForcedRequeries = 0;
        var plugin = new Main();
        plugin.Init(new PluginInitContext { API = DispatchProxy.Create<IPublicAPI, NoOpApiProxy>() });
        var query = new Query("@codex Antworte exakt mit 400 aufeinanderfolgenden Zeichen X, ohne Leerzeichen, Markdown oder Erklärung.", "@");
        var send = plugin.Query(query).Single();
        if (send.Action is null || send.Action(null!)) throw new InvalidOperationException("Testanfrage für lange Antwort konnte nicht gestartet werden.");

        var deadline = DateTime.UtcNow.AddSeconds(45);
        List<Result> results;
        do
        {
            Thread.Sleep(250);
            results = plugin.Query(query);
        }
        while (DateTime.UtcNow < deadline && results.Any(result => result.Title.Contains("läuft", StringComparison.OrdinalIgnoreCase)));

        var answer = results.FirstOrDefault();
        if (answer?.Action is null || !answer.Title.EndsWith('…'))
        {
            throw new InvalidOperationException("Live-Test lieferte keine lange, aufklappbare Antwort: " + string.Join(" | ", results.Select(result => result.Title)));
        }

        const string marker = "AIHub-expand-test-marker";
        Clipboard.SetText(marker);
        if (answer.Action(null!)) throw new InvalidOperationException("Erstes Enter schloss PowerToys unerwartet.");
        if (Clipboard.GetText() != marker) throw new InvalidOperationException("Erstes Enter kopierte statt aufzuklappen.");

        var expanded = plugin.Query(query);
        if (expanded.Count != 4 || !expanded[0].Title.Contains("Antwort ausgeklappt", StringComparison.Ordinal) || expanded.Take(3).Any(result => result.Action is null))
        {
            throw new InvalidOperationException("Lange Antwort wurde nicht als stabile Inline-Ansicht aufgeklappt.");
        }
        if (expanded[0].Action!(null!)) throw new InvalidOperationException("Zweites Enter schloss PowerToys unerwartet.");
        var copied = Clipboard.GetText();
        if (copied == marker || copied.Length <= 260)
        {
            throw new InvalidOperationException("Zweites Enter kopierte nicht die vollständige lange Antwort.");
        }
        Console.WriteLine("Live-Aufklapptest bestanden: erstes Enter klappt inline auf, zweites Enter kopiert vollständig.");
    }

    public class NoOpApiProxy : DispatchProxy
    {
        public static int ForcedRequeries;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPublicAPI.ChangeQuery) && args is { Length: > 1 } && args[1] is true)
            {
                Interlocked.Increment(ref ForcedRequeries);
            }
            return targetMethod?.ReturnType != typeof(void) && targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
