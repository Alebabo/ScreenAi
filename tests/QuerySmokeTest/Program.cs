using System.Diagnostics;
using System.Reflection;
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
            if (results.Count != 1 || results[0].Action is null)
            {
                throw new InvalidOperationException("Gültige Eingabe erzeugt kein startbares Ergebnis.");
            }
        }

        var choices = plugin.Query(new Query("@", "@"));
        if (choices.Count != 4 || choices.Any(result => result.Action is not null))
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
        if (inlineResult.Count != 1 || inlineResult[0].Action is null || inlineResult[0].Title != "An alle drei senden")
        {
            throw new InvalidOperationException("Parallelanfrage erzeugt kein Inline-Senden-Ergebnis.");
        }

        Thread.Sleep(250);
        var started = AiProcessIds().Except(before).ToArray();
        if (started.Length != 0)
        {
            throw new InvalidOperationException($"Tippen oder Chatfenster startete unerwartete Prozesse: {string.Join(", ", started)}");
        }

        Console.WriteLine("Query-Smoke-Test bestanden: 1000 Eingaben, Inline-Senden für Parallelmodus, 0 Modellprozesse vor Enter.");

        if (args.Contains("--live")) RunLiveInlineTest();
    }

    static void RunLiveInlineTest()
    {
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
        Console.WriteLine("Live-Inline-Test bestanden: drei Antworten plus gemeinsame Rückfrage direkt als PowerToys-Ergebnisse.");
    }

    public class NoOpApiProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType != typeof(void) && targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}
