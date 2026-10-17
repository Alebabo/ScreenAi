using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Wox.Plugin;

namespace Community.PowerToys.Run.Plugin.AIHub;

public sealed class Main : IPlugin
{
    const int ResultScore = 1_000_000;
    const int CompactAnswerLimit = 260;
    const int ExpandedChunkLimit = 90;
    const int ExpandedChunksPerPage = 2;
    static readonly string[] AllProviders = ["gemini", "claude", "codex"];
    static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = "Gemini", ["claude"] = "Claude", ["codex"] = "Codex"
    };

    readonly object sync = new();
    readonly Dictionary<string, RequestState> requests = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, List<ChatMessage>>> continuations = new(StringComparer.OrdinalIgnoreCase);
    IPublicAPI? api;

    public static string PluginID => "F50D9A0DE73943768D8C77C065E7B6E9";
    public string Name => "AI Hub";
    public string Description => "Gemini, Claude und Codex direkt in PowerToys Run";
    public void Init(PluginInitContext context) => api = context.API;

    public List<Result> Query(Query query)
    {
        var parts = query.Search.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ModeChoices();

        var mode = parts[0].ToLowerInvariant();
        if (mode is not ("gemini" or "claude" or "codex" or "all")) return [];

        var prompt = parts.Length == 2 ? parts[1].Trim() : "";
        if (prompt.Length == 0)
        {
            return [new Result
            {
                Title = mode == "all" ? "Frage an alle drei eingeben" : $"Frage an {Labels[mode]} eingeben",
                SubTitle = "Tippen startet noch keine Anfrage.",
                Score = ResultScore,
                IcoPath = ModeIcon(mode)
            }];
        }

        var key = mode + "\n" + prompt;
        RequestState? state;
        lock (sync) requests.TryGetValue(key, out state);
        if (state is null) return [CreateSendResult(key, mode, prompt, query.RawQuery)];
        return ResultsFor(state, query.RawQuery);
    }

    static List<Result> ModeChoices() =>
    [
        Choice("Gemini", "@gemini deine Frage", "gemini"),
        Choice("Claude", "@claude deine Frage", "claude"),
        Choice("Codex", "@codex deine Frage", "codex"),
        Choice("Alle drei parallel", "@ all deine Frage", null)
    ];

    static Result Choice(string title, string example, string? provider) => new()
    {
        Title = title,
        SubTitle = example,
        Score = ResultScore,
        IcoPath = provider is null ? "Images/all.png" : ProviderIcon(provider)
    };

    Result CreateSendResult(string key, string mode, string prompt, string rawQuery) => new()
    {
        Title = mode == "all" ? "An alle drei senden" : $"An {Labels[mode]} senden",
        SubTitle = prompt,
        Score = ResultScore,
        IcoPath = ModeIcon(mode),
        Action = _ =>
        {
            StartRequest(key, mode, prompt, rawQuery);
            return false;
        }
    };

    List<Result> ResultsFor(RequestState state, string rawQuery)
    {
        List<ProviderSnapshot> snapshots;
        bool completed;
        string? expandedProvider;
        lock (sync)
        {
            snapshots = state.Providers.Select(provider => new ProviderSnapshot(
                provider,
                state.Results[provider].Status,
                state.Results[provider].Text)).ToList();
            completed = state.Completed;
            expandedProvider = state.ExpandedProvider;
        }

        if (expandedProvider is not null)
        {
            var expanded = snapshots.FirstOrDefault(snapshot =>
                snapshot.Status == ProviderStatus.Succeeded &&
                snapshot.Provider.Equals(expandedProvider, StringComparison.OrdinalIgnoreCase));
            if (expanded is not null) return ExpandedResults(state, expanded, rawQuery);
        }

        var results = snapshots.Select(snapshot => ProviderResult(state, snapshot, rawQuery)).ToList();
        if (!completed)
        {
            results.Add(new Result
            {
                Title = "Abbrechen",
                SubTitle = "Beendet alle laufenden Anbieter dieser Anfrage.",
                Score = ResultScore,
                IcoPath = ModeIcon(state.Mode),
                Action = _ =>
                {
                    Cancel(state, rawQuery);
                    return false;
                }
            });
        }
        else if (state.Canceled)
        {
            results.Add(new Result
            {
                Title = "Neue Frage eingeben",
                SubTitle = "Die abgebrochene Anfrage wird nicht als Kontext übernommen.",
                Score = ResultScore,
                IcoPath = ModeIcon(state.Mode),
                Action = _ =>
                {
                    api?.ChangeQuery(CommandPrefix(state.Mode), false);
                    return false;
                }
            });
        }
        else
        {
            results.Add(new Result
            {
                Title = state.Mode == "all" ? "Gemeinsame Rückfrage stellen" : $"Rückfrage an {Labels[state.Mode]} stellen",
                SubTitle = "Übernimmt den bisherigen Gesprächskontext.",
                Score = ResultScore,
                IcoPath = ModeIcon(state.Mode),
                Action = _ =>
                {
                    PrepareContinuation(state);
                    api?.ChangeQuery(CommandPrefix(state.Mode), false);
                    return false;
                }
            });
        }
        return results;
    }

    Result ProviderResult(RequestState state, ProviderSnapshot snapshot, string rawQuery)
    {
        var label = Labels[snapshot.Provider];
        var text = snapshot.Text;
        var compactText = Compact(text, CompactAnswerLimit);
        var isLong = snapshot.Status == ProviderStatus.Succeeded && Normalize(text).Length > CompactAnswerLimit;
        var title = snapshot.Status switch
        {
            ProviderStatus.Running => $"{label}: Anfrage läuft …",
            ProviderStatus.Canceled => $"{label}: Abgebrochen",
            ProviderStatus.Failed => $"{label}: Fehler — {Compact(text, 220)}",
            _ => $"{label}: {compactText}"
        };
        return new Result
        {
            Title = title,
            SubTitle = snapshot.Status == ProviderStatus.Succeeded
                ? isLong
                    ? "Enter klappt die vollständige Antwort auf. Darüberfahren zeigt den Volltext."
                    : "Enter kopiert die vollständige Antwort. Darüberfahren zeigt den Volltext."
                : text,
            Score = ResultScore,
            ToolTipData = string.IsNullOrWhiteSpace(text) ? null : new ToolTipData(label, text),
            IcoPath = ProviderIcon(snapshot.Provider),
            Action = snapshot.Status == ProviderStatus.Succeeded ? _ =>
            {
                if (isLong)
                {
                    lock (sync)
                    {
                        state.ExpandedProvider = snapshot.Provider;
                        state.ExpandedPage = 0;
                    }
                    api?.ChangeQuery(rawQuery, true);
                }
                else
                {
                    CopyToClipboard(text);
                }
                return false;
            } : null
        };
    }

    List<Result> ExpandedResults(RequestState state, ProviderSnapshot snapshot, string rawQuery)
    {
        var label = Labels[snapshot.Provider];
        var chunks = SplitForDisplay(snapshot.Text, ExpandedChunkLimit);
        var totalPages = Math.Max(1, (int)Math.Ceiling(chunks.Count / (double)ExpandedChunksPerPage));
        int page;
        lock (sync)
        {
            state.ExpandedPage = Math.Clamp(state.ExpandedPage, 0, totalPages - 1);
            page = state.ExpandedPage;
        }

        Func<ActionContext, bool> copy = _ =>
        {
            CopyToClipboard(snapshot.Text);
            return false;
        };
        var results = new List<Result>
        {
            new()
            {
                Title = $"{label}: Antwort ausgeklappt",
                SubTitle = $"Enter kopiert vollständig · Seite {page + 1}/{totalPages}",
                Score = ResultScore,
                ToolTipData = new ToolTipData(label, snapshot.Text),
                IcoPath = ProviderIcon(snapshot.Provider),
                Action = copy
            }
        };

        var firstChunk = page * ExpandedChunksPerPage;
        foreach (var (chunk, offset) in chunks.Skip(firstChunk).Take(ExpandedChunksPerPage).Select((chunk, offset) => (chunk, offset)))
        {
            results.Add(new Result
            {
                Title = chunk,
                SubTitle = $"Teil {firstChunk + offset + 1}/{chunks.Count} · Enter kopiert die vollständige Antwort.",
                Score = ResultScore - offset - 1,
                ToolTipData = new ToolTipData(label, snapshot.Text),
                IcoPath = ProviderIcon(snapshot.Provider),
                Action = copy
            });
        }

        var hasNextPage = page + 1 < totalPages;
        results.Add(new Result
        {
            Title = hasNextPage ? "Nächste Seite" : "Zurück zur Ergebnisübersicht",
            SubTitle = hasNextPage ? $"Zeigt Seite {page + 2}/{totalPages}." : "Zeigt wieder alle Anbieter.",
            Score = ResultScore - ExpandedChunksPerPage - 1,
            IcoPath = "Images/icon.png",
            Action = _ =>
            {
                lock (sync)
                {
                    if (hasNextPage) state.ExpandedPage++;
                    else
                    {
                        state.ExpandedProvider = null;
                        state.ExpandedPage = 0;
                    }
                }
                api?.ChangeQuery(rawQuery, true);
                return false;
            }
        });
        return results;
    }

    internal static List<string> SplitForDisplay(string text, int limit = ExpandedChunkLimit)
    {
        var remaining = Normalize(text);
        var chunks = new List<string>();
        while (remaining.Length > limit)
        {
            var cut = remaining.LastIndexOf(' ', limit);
            if (cut < limit / 2) cut = limit;
            chunks.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].TrimStart();
        }
        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    static string Compact(string text, int limit)
    {
        var compact = Normalize(text);
        return compact.Length <= limit ? compact : compact[..(limit - 1)] + "…";
    }

    static string Normalize(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string CommandPrefix(string mode) => mode == "all" ? "@ all " : $"@{mode} ";
    static string ModeIcon(string mode) => mode == "all" ? "Images/all.png" : ProviderIcon(mode);
    static string ProviderIcon(string provider) => $"Images/{provider}.png";

    void StartRequest(string key, string mode, string prompt, string rawQuery)
    {
        RequestState state;
        lock (sync)
        {
            if (requests.ContainsKey(key)) return;
            var providers = mode == "all" ? AllProviders : [mode];
            var histories = continuations.Remove(mode, out var previous)
                ? previous.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
                : providers.ToDictionary(provider => provider, _ => new List<ChatMessage>());
            foreach (var provider in providers) histories.TryAdd(provider, []);
            state = new RequestState(key, mode, prompt, rawQuery, providers, histories);
            requests[key] = state;
            foreach (var old in requests.Values.OrderBy(item => item.Created).Take(Math.Max(0, requests.Count - 20)).ToArray())
            {
                if (old.Completed) requests.Remove(old.Key);
            }
        }
        api?.ChangeQuery(rawQuery, true);
        _ = RunRequest(state);
    }

    async Task RunRequest(RequestState state)
    {
        await Task.WhenAll(state.Providers.Select(provider => RunProvider(state, provider)));
        lock (sync) state.Completed = true;
        api?.ChangeQuery(state.RawQuery, true);
    }

    async Task RunProvider(RequestState state, string provider)
    {
        Process? process = null;
        try
        {
            var baseDir = Path.GetDirectoryName(typeof(Main).Assembly.Location)!;
            var start = new ProcessStartInfo(FindNode())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = baseDir
            };
            start.ArgumentList.Add(Path.Combine(baseDir, "bridge.mjs"));
            process = new Process { StartInfo = start };
            process.Start();
            lock (sync) state.Results[provider].Process = process;

            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                provider,
                prompt = state.Prompt,
                history = state.Histories[provider].Select(message => new { role = message.Role, text = message.Text })
            }));
            process.StandardInput.Close();

            string answer = "";
            string error = "";
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var evt = JsonDocument.Parse(line);
                if (!evt.RootElement.TryGetProperty("type", out var typeValue) || !evt.RootElement.TryGetProperty("text", out var textValue)) continue;
                if (typeValue.GetString() == "answer") answer = textValue.GetString() ?? "";
                if (typeValue.GetString() == "error") error = textValue.GetString() ?? "Anfrage fehlgeschlagen.";
            }

            await process.WaitForExitAsync();
            var stderr = await errors;
            lock (sync)
            {
                var result = state.Results[provider];
                if (state.Canceled)
                {
                    result.Status = ProviderStatus.Canceled;
                    result.Text = "Anfrage wurde abgebrochen.";
                }
                else if (process.ExitCode == 0 && answer.Length > 0)
                {
                    result.Status = ProviderStatus.Succeeded;
                    result.Text = answer.Trim();
                    state.Histories[provider].Add(new ChatMessage("user", state.Prompt));
                    state.Histories[provider].Add(new ChatMessage("assistant", answer.Trim()));
                }
                else
                {
                    result.Status = ProviderStatus.Failed;
                    result.Text = error.Length > 0 ? error : "Anfrage fehlgeschlagen. " + stderr[..Math.Min(stderr.Length, 500)];
                }
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                var result = state.Results[provider];
                result.Status = state.Canceled ? ProviderStatus.Canceled : ProviderStatus.Failed;
                result.Text = state.Canceled ? "Anfrage wurde abgebrochen." : ex.Message;
            }
        }
        finally
        {
            lock (sync) state.Results[provider].Process = null;
            process?.Dispose();
            api?.ChangeQuery(state.RawQuery, true);
        }
    }

    void PrepareContinuation(RequestState state)
    {
        lock (sync)
        {
            continuations[state.Mode] = state.Histories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        }
    }

    void Cancel(RequestState state, string rawQuery)
    {
        Process[] processes;
        lock (sync)
        {
            state.Canceled = true;
            processes = state.Results.Values.Select(result => result.Process).OfType<Process>().ToArray();
            foreach (var result in state.Results.Values.Where(result => result.Status == ProviderStatus.Running))
            {
                result.Status = ProviderStatus.Canceled;
                result.Text = "Anfrage wurde abgebrochen.";
            }
        }
        foreach (var process in processes)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        api?.ChangeQuery(rawQuery, true);
    }

    static string FindNode()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "node";
    }

    sealed record ChatMessage(string Role, string Text);
    sealed record ProviderSnapshot(string Provider, ProviderStatus Status, string Text);
    enum ProviderStatus { Running, Succeeded, Failed, Canceled }

    sealed class ProviderResultState
    {
        public ProviderStatus Status { get; set; } = ProviderStatus.Running;
        public string Text { get; set; } = "Warte auf Antwort …";
        public Process? Process { get; set; }
    }

    sealed class RequestState
    {
        public RequestState(string key, string mode, string prompt, string rawQuery, string[] providers, Dictionary<string, List<ChatMessage>> histories)
        {
            Key = key;
            Mode = mode;
            Prompt = prompt;
            RawQuery = rawQuery;
            Providers = providers;
            Histories = histories;
            Results = providers.ToDictionary(provider => provider, _ => new ProviderResultState());
        }

        public string Key { get; }
        public string Mode { get; }
        public string Prompt { get; }
        public string RawQuery { get; }
        public string[] Providers { get; }
        public Dictionary<string, List<ChatMessage>> Histories { get; }
        public Dictionary<string, ProviderResultState> Results { get; }
        public DateTime Created { get; } = DateTime.UtcNow;
        public bool Completed { get; set; }
        public bool Canceled { get; set; }
        public string? ExpandedProvider { get; set; }
        public int ExpandedPage { get; set; }
    }
}
