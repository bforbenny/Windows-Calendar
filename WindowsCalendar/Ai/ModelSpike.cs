using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using Serilog;

namespace WindowsCalendar.Ai;

/// <summary>
/// NPU spike gate (on-device-model R12): runs the embedded phrase set through the Windows AI
/// <see cref="LanguageModel"/> in three modes and writes <c>spike-report.md</c> to LocalState.
/// Phrases, prompts and model output go only to the report, never to the log (on-device-model R9).
/// </summary>
internal static class ModelSpike
{
    /// <summary>Every request is bounded so the known hang (WindowsAppSDK issue #6871) shows as a failure (on-device-model R12).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // EnsureReadyAsync and CreateAsync hung for over 9 minutes on 2026-11-11; bound them so a hang is reported.
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(2);

    private const double MaxWarmP95Ms = 1500;
    private const double MaxFailureRate = 0.05;

    private enum Mode { StructuredFull, StructuredReduced, Plain }

    private static readonly Mode[] Modes = [Mode.StructuredFull, Mode.StructuredReduced, Mode.Plain];

    private sealed record Sample(string Phrase, Mode Mode, long Ms, string Outcome, string DateMode, string Start)
    {
        public bool Ok => Outcome == "ok";
    }

    private static readonly string[] DateModes = ["none", "day_offset", "weekday_weeks", "nth_weekday", "month_day"];

    private static readonly string[] ReducedFields = ["dateMode", "dayOffset", "weekday", "weeksAhead", "nth", "month", "day", "start", "end"];

    private static readonly string[] FullFields =
    [
        "kind", "title", .. ReducedFields, "durationMinutes", "allDay", "location", "calendarHint", "repeat", "importance",
    ];

    /// <summary>Relative-date and time properties shared by both schemas (nl-quick-add R6, on-device-model R8).</summary>
    private const string DateTimeProperties = """
        "dateMode": { "type": "string", "enum": ["none", "day_offset", "weekday_weeks", "nth_weekday", "month_day"] },
        "dayOffset": { "type": "integer" },
        "weekday": { "type": "integer" },
        "weeksAhead": { "type": "integer" },
        "nth": { "type": "integer" },
        "month": { "type": "integer" },
        "day": { "type": "integer" },
        "start": { "type": "string" },
        "end": { "type": "string" }
        """;

    /// <summary>Full nl-quick-add R6 schema; only type, properties, required and enum are used (on-device-model R1).</summary>
    private static readonly string FullSchema = $$"""
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["event", "task"] },
            "title": { "type": "string" },
            {{DateTimeProperties}},
            "durationMinutes": { "type": "integer" },
            "allDay": { "type": "boolean" },
            "location": { "type": "string" },
            "calendarHint": { "type": "string" },
            "repeat": { "type": "string", "enum": ["none", "daily", "weekdays", "weekly", "monthly"] },
            "importance": { "type": "string", "enum": ["low", "normal", "high"] }
          },
          "required": [{{string.Join(", ", FullFields.Select(f => $"\"{f}\""))}}]
        }
        """;

    /// <summary>Reduced schema: only the relative date fields plus start and end (on-device-model R12).</summary>
    private static readonly string ReducedSchema = $$"""
        {
          "type": "object",
          "properties": {
            {{DateTimeProperties}}
          },
          "required": [{{string.Join(", ", ReducedFields.Select(f => $"\"{f}\""))}}]
        }
        """;

    /// <summary>
    /// Runs the spike and writes <c>&lt;localStateDir&gt;\spike-report.md</c>. Never throws for model
    /// failures; they are recorded in the report. Callers run it with <c>Task.Run</c>.
    /// </summary>
    public static async Task RunAsync(string localStateDir, CancellationToken ct)
    {
        Log.Information("Model spike started");
        var report = new StringBuilder();
        report.AppendLine("# On-device model spike (on-device-model R12)").AppendLine();
        report.AppendLine($"- Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"- Machine: {Environment.MachineName}, OS {Environment.OSVersion.Version}");

        LanguageModel? model = null;
        try
        {
            AIFeatureReadyState state;
            try
            {
                state = LanguageModel.GetReadyState();
            }
            catch (Exception ex)
            {
                Unavailable(report, localStateDir, "GetReadyState", ex);
                return;
            }
            report.AppendLine($"- Ready state: {state}");
            Log.Information("Model spike: ready state {State}", state);

            // DisabledByUser is also tried: the spike saw it with the model installed and consent allowed.
            if (state is AIFeatureReadyState.NotReady or AIFeatureReadyState.DisabledByUser)
            {
                var sw = Stopwatch.StartNew();
                AIFeatureReadyResult result;
                Log.Information("Model spike: EnsureReadyAsync called");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(SetupTimeout);
                    result = await LanguageModel.EnsureReadyAsync().AsTask(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Finish(report, localStateDir, $"**NO-GO**: EnsureReadyAsync did not complete within {SetupTimeout.TotalSeconds:0} s.");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Unavailable(report, localStateDir, "EnsureReadyAsync", ex);
                    return;
                }
                sw.Stop();
                report.Append($"- EnsureReady: {result.Status} in {sw.ElapsedMilliseconds} ms");
                if (result.Status != AIFeatureReadyResultState.Success)
                {
                    report.Append($" ({result.ExtendedError?.GetType().Name} 0x{result.ExtendedError?.HResult:X8}: {result.ErrorDisplayText})");
                }
                report.AppendLine();
                if (result.Status != AIFeatureReadyResultState.Success)
                {
                    Finish(report, localStateDir, "**NO-GO**: the model is not ready.");
                    return;
                }
            }
            else
            {
                report.AppendLine("- EnsureReady: not called");
                if (state != AIFeatureReadyState.Ready)
                {
                    Finish(report, localStateDir, $"**NO-GO**: the model is unavailable ({state}).");
                    return;
                }
            }

            var create = Stopwatch.StartNew();
            Log.Information("Model spike: CreateAsync called");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SetupTimeout);
                model = await LanguageModel.CreateAsync().AsTask(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Finish(report, localStateDir, $"**NO-GO**: CreateAsync did not complete within {SetupTimeout.TotalSeconds:0} s.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Unavailable(report, localStateDir, "CreateAsync", ex);
                return;
            }
            create.Stop();
            report.AppendLine($"- CreateAsync (cold): {create.ElapsedMilliseconds} ms");
            report.AppendLine($"- Request timeout: {RequestTimeout.TotalSeconds:0} s; options Temperature 0, TopK 1");

            var phrases = LoadPhrases();
            report.AppendLine($"- Phrases: {phrases.Count}").AppendLine();

            var options = new LanguageModelOptions { Temperature = 0, TopK = 1 };
            var samples = new List<Sample>();
            foreach (var mode in Modes)
            {
                foreach (var phrase in phrases)
                {
                    ct.ThrowIfCancellationRequested();
                    samples.Add(await RunOneAsync(model, mode, phrase, options, ct));
                }
            }

            var verdicts = WriteSummary(report, samples);
            WriteTable(report, samples);
            Finish(report, localStateDir, string.Join(Environment.NewLine + Environment.NewLine, verdicts));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Information("Model spike cancelled");
        }
        catch (Exception ex)
        {
            // Error type only; the message could carry model output (on-device-model R9).
            Log.Error("Model spike failed: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            model?.Dispose();
        }
    }

    /// <summary>One request, bounded by <see cref="RequestTimeout"/>; the async operation is cancelled on timeout.</summary>
    private static async Task<Sample> RunOneAsync(LanguageModel model, Mode mode, string phrase, LanguageModelOptions options, CancellationToken ct)
    {
        var prompt = BuildPrompt(mode, phrase);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            string text;
            if (mode == Mode.Plain)
            {
                var task = model.GenerateResponseAsync(prompt, options).AsTask(requestCts.Token);
                var result = await task.WaitAsync(RequestTimeout, ct);
                sw.Stop();
                switch (result.Status)
                {
                    case LanguageModelResponseStatus.Complete:
                        text = StripCodeFence(result.Text);
                        break;
                    case LanguageModelResponseStatus.PromptBlockedByContentModeration:
                    case LanguageModelResponseStatus.ResponseBlockedByContentModeration:
                        return new Sample(phrase, mode, sw.ElapsedMilliseconds, "moderation blocked", "", "");
                    default:
                        return new Sample(phrase, mode, sw.ElapsedMilliseconds, ErrorOutcome(result.Status.ToString(), result.ExtendedError), "", "");
                }
            }
            else
            {
                var schema = mode == Mode.StructuredFull ? FullSchema : ReducedSchema;
                var task = model.GenerateStructuredJsonResponseAsync(prompt, schema, options).AsTask(requestCts.Token);
                var result = await task.WaitAsync(RequestTimeout, ct);
                sw.Stop();
                switch (result.Status)
                {
                    case GenerateStructuredJsonResponseStatus.Complete:
                        text = result.Text;
                        break;
                    case GenerateStructuredJsonResponseStatus.CompleteWithInvalidStructure:
                        return new Sample(phrase, mode, sw.ElapsedMilliseconds, "invalid JSON", "", "");
                    case GenerateStructuredJsonResponseStatus.PromptBlockedByContentModeration:
                    case GenerateStructuredJsonResponseStatus.ResponseBlockedByContentModeration:
                        return new Sample(phrase, mode, sw.ElapsedMilliseconds, "moderation blocked", "", "");
                    default:
                        return new Sample(phrase, mode, sw.ElapsedMilliseconds, ErrorOutcome(result.Status.ToString(), result.ExtendedError), "", "");
                }
            }
            return Parse(phrase, mode, sw.ElapsedMilliseconds, text);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            requestCts.Cancel();
            return new Sample(phrase, mode, sw.ElapsedMilliseconds, "timeout", "", "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            sw.Stop();
            return new Sample(phrase, mode, sw.ElapsedMilliseconds, $"error {ex.GetType().Name}", "", "");
        }
    }

    private static string ErrorOutcome(string status, Exception? extended) =>
        extended is null ? $"error {status}" : $"error {status} {extended.GetType().Name} 0x{extended.HResult:X8}";

    /// <summary>
    /// Parses with System.Text.Json and checks the object has every field of its schema and a known
    /// date mode; anything else is invalid JSON (on-device-model R6).
    /// </summary>
    private static Sample Parse(string phrase, Mode mode, long ms, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var fields = mode == Mode.StructuredReduced ? ReducedFields : FullFields;
            if (root.ValueKind != JsonValueKind.Object || fields.Any(f => !root.TryGetProperty(f, out _)))
            {
                return new Sample(phrase, mode, ms, "invalid JSON", "", "");
            }
            var dateMode = root.GetProperty("dateMode");
            var start = root.GetProperty("start");
            if (dateMode.ValueKind != JsonValueKind.String || !DateModes.Contains(dateMode.GetString())
                || start.ValueKind != JsonValueKind.String)
            {
                return new Sample(phrase, mode, ms, "invalid JSON", "", "");
            }
            return new Sample(phrase, mode, ms, "ok", dateMode.GetString()!, start.GetString()!);
        }
        catch (JsonException)
        {
            return new Sample(phrase, mode, ms, "invalid JSON", "", "");
        }
    }

    /// <summary>Plain generation may wrap the object in a Markdown code fence; only that wrapper is removed.</summary>
    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstLine = t.IndexOf('\n');
        if (firstLine < 0) return t;
        t = t[(firstLine + 1)..];
        if (t.TrimEnd().EndsWith("```", StringComparison.Ordinal)) t = t.TrimEnd()[..^3];
        return t.Trim();
    }

    /// <summary>
    /// Prompt with the fixed reference time and names (on-device-model R8). The model never returns
    /// absolute dates, only the relative forms of nl-quick-add R6.
    /// </summary>
    private static string BuildPrompt(Mode mode, string phrase)
    {
        var full = mode != Mode.StructuredReduced;
        var sb = new StringBuilder();
        sb.AppendLine("Convert the calendar quick-add phrase into one JSON object.");
        sb.AppendLine("Now: Wednesday 11 November 2026 09:00. Time zone: W. Australia Standard Time.");
        if (full) sb.AppendLine("Calendars: Work, Personal. Task lists: Tasks.");
        sb.AppendLine("Never give an absolute date. Describe the date with dateMode:");
        sb.AppendLine("- \"none\": the phrase has no date.");
        sb.AppendLine("- \"day_offset\": dayOffset days from today (0 today, 1 tomorrow).");
        sb.AppendLine("- \"weekday_weeks\": weekday (0 Sunday to 6 Saturday), weeksAhead weeks after its next occurrence (0 = the next one).");
        sb.AppendLine("- \"nth_weekday\": the nth (1 to 5, or -1 for last) weekday (0 Sunday to 6 Saturday) of month (1 to 12).");
        sb.AppendLine("- \"month_day\": day (1 to 31) of month (1 to 12).");
        sb.AppendLine("start and end: 24-hour \"HH:mm\", or \"\" when not given.");
        if (full)
        {
            sb.AppendLine("kind: \"task\" for a to-do, else \"event\". title: the phrase words that name the item.");
            sb.AppendLine("durationMinutes: 0 when not given. allDay: true only for an all-day item.");
            sb.AppendLine("location, calendarHint (one of the calendar or task list names): \"\" when not given.");
            sb.AppendLine("repeat: none, daily, weekdays, weekly or monthly. importance: low, normal or high.");
        }
        sb.AppendLine("Unused numbers are 0 and unused text is \"\".");
        if (mode == Mode.Plain)
        {
            sb.AppendLine($"Reply with only the JSON object, no other text, with exactly these keys: {string.Join(", ", FullFields)}.");
        }
        sb.Append("Phrase: ").Append(phrase);
        return sb.ToString();
    }

    private static List<string> LoadPhrases()
    {
        using var stream = typeof(ModelSpike).Assembly.GetManifestResourceStream("WindowsCalendar.Ai.spike-phrases.txt")
            ?? throw new InvalidOperationException("Spike phrase resource missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var phrases = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0) phrases.Add(line.Trim());
        }
        return phrases;
    }

    /// <summary>Per-mode statistics and the R12 verdict for each mode.</summary>
    private static List<string> WriteSummary(StringBuilder report, List<Sample> samples)
    {
        var verdicts = new List<string>();
        report.AppendLine("## Summary per mode").AppendLine();
        report.AppendLine("| Mode | Count | Cold ms | Warm p50 ms | Warm p95 ms | Warm ok | Invalid JSON | Timeouts | Blocked | Errors | Failure rate |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var mode in Modes)
        {
            var list = samples.Where(s => s.Mode == mode).ToList();
            if (list.Count == 0) continue;
            var warm = list.Skip(1).Where(s => s.Ok).Select(s => (double)s.Ms).Order().ToList();
            var invalid = list.Count(s => s.Outcome == "invalid JSON");
            var timeouts = list.Count(s => s.Outcome == "timeout");
            var blocked = list.Count(s => s.Outcome == "moderation blocked");
            var errors = list.Count(s => s.Outcome.StartsWith("error", StringComparison.Ordinal));
            var invalidRate = (double)invalid / list.Count;
            var failureRate = (double)(invalid + timeouts + blocked + errors) / list.Count;
            var p50 = Percentile(warm, 0.50);
            var p95 = Percentile(warm, 0.95);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {Name(mode)} | {list.Count} | {list[0].Ms} ({list[0].Outcome}) | {Fmt(p50)} | {Fmt(p95)} | {warm.Count} | {invalid} ({invalidRate:P1}) | {timeouts} | {blocked} | {errors} | {failureRate:P1} |"));

            var go = p95 is { } v && v <= MaxWarmP95Ms && failureRate <= MaxFailureRate;
            verdicts.Add(string.Create(CultureInfo.InvariantCulture,
                $"**{(go ? "GO" : "NO-GO")}** ({Name(mode)}): warm p95 {Fmt(p95)} ms (limit {MaxWarmP95Ms:0}), invalid JSON {invalidRate:P1}, failures incl. timeouts, blocks and errors {failureRate:P1} (limit {MaxFailureRate:P0})."));
        }
        report.AppendLine();
        report.AppendLine("Cold = first request of the mode (any outcome). Warm = the rest, ok results only. Failure rate counts invalid JSON, timeouts, moderation blocks and errors.").AppendLine();
        return verdicts;
    }

    private static void WriteTable(StringBuilder report, List<Sample> samples)
    {
        report.AppendLine("## Per phrase").AppendLine();
        report.AppendLine("| Phrase | Mode | ms | Outcome | dateMode | start |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var s in samples)
        {
            report.AppendLine($"| {Cell(s.Phrase)} | {Name(s.Mode)} | {s.Ms} | {Cell(s.Outcome)} | {Cell(s.DateMode)} | {Cell(s.Start)} |");
        }
        report.AppendLine();
    }

    /// <summary>Nearest-rank percentile; null when there are no samples.</summary>
    private static double? Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static string Fmt(double? ms) => ms is { } v ? v.ToString("0", CultureInfo.InvariantCulture) : "n/a";

    private static string Name(Mode mode) => mode switch
    {
        Mode.StructuredFull => "structured, full schema",
        Mode.StructuredReduced => "structured, reduced schema",
        _ => "plain + System.Text.Json",
    };

    private static string Cell(string text) => text.Replace("|", "\\|").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Records an API exception as "unavailable" (on-device-model R2) and writes the report.</summary>
    private static void Unavailable(StringBuilder report, string localStateDir, string step, Exception ex)
    {
        Log.Warning("Model spike: {Step} failed with {ErrorType}", step, ex.GetType().Name);
        report.AppendLine($"- Unavailable: {step} threw {ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}");
        Finish(report, localStateDir, "**NO-GO**: the LanguageModel API is unavailable.");
    }

    private static void Finish(StringBuilder report, string localStateDir, string verdict)
    {
        report.AppendLine();
        report.AppendLine("## Go / no-go (on-device-model R12)").AppendLine();
        report.AppendLine(verdict);
        File.WriteAllText(Path.Combine(localStateDir, "spike-report.md"), report.ToString(), new UTF8Encoding(false));
        Log.Information("Model spike finished");
    }
}
