using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Whisper.net;

// args: <segdir> <model> <out> <threads> [prompt]; GLUE=1 — куски до 30 с, как в meeting refine
var segDir = args[0];
var threads = int.Parse(args[3], CultureInfo.InvariantCulture);
var prompt = args.Length > 4 ? args[4] : null;
var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(segDir, "segments.json"))).RootElement;
var sw = Stopwatch.StartNew();
using var factory = WhisperFactory.FromPath(args[1]);
var builder = factory.CreateBuilder().WithLanguage("ru").WithNoContext().WithThreads(threads);
if (prompt is not null)
{
    builder = builder.WithPrompt(prompt);
}

await using var processor = builder.Build();
var load = sw.Elapsed.TotalSeconds;
var lines = new List<string>();
var per = new List<double>();
double speech = 0;
sw.Restart();
// GLUE=1 — как `meeting refine`: подряд идущие фразы кусками, пока речь куска не больше окна Whisper (30 с).
var glue = Environment.GetEnvironmentVariable("GLUE") == "1";
var groups = new List<List<JsonElement>>();
double groupSpeech = 0;
foreach (var m in meta.EnumerateArray())
{
    var len = m.GetProperty("len").GetDouble();
    speech += len;
    if (groups.Count == 0 || !glue || groupSpeech + len > 30)
    {
        groups.Add([]);
        groupSpeech = 0;
    }

    groups[^1].Add(m);
    groupSpeech += len;
}

foreach (var group in groups)
{
    var one = Stopwatch.StartNew();
    var text = new List<string>();
    if (glue)
    {
        // Фразы из bench.py — WAV PCM 16 моно 16 кГц с заголовком 44 байта.
        var samples = group
            .SelectMany(m => File.ReadAllBytes(Path.Combine(segDir, $"{m.GetProperty("i").GetInt32():0000}.wav"))[44..]
                .Chunk(2)
                .Select(b => BitConverter.ToInt16(b) / 32768f))
            .ToArray();
        await foreach (var s in processor.ProcessAsync(samples))
        {
            text.Add(s.Text.Trim());
        }
    }
    else
    {
        await using var fs = File.OpenRead(Path.Combine(segDir, $"{group[0].GetProperty("i").GetInt32():0000}.wav"));
        await foreach (var s in processor.ProcessAsync(fs))
        {
            text.Add(s.Text.Trim());
        }
    }

    per.Add(one.Elapsed.TotalSeconds);
    lines.Add($"[{group[0].GetProperty("start").GetDouble(),7:F1}] {string.Join(' ', text)}");
}
var total = sw.Elapsed.TotalSeconds;
per.Sort();
File.WriteAllLines(args[2], lines);
Console.WriteLine($"[turbo t={threads}{(prompt is null ? "" : " prompt")}] загрузка {load:F1} с, распознавание {total:F1} с, на секунду речи {total / speech:F3}, на фразу p50 {per[per.Count / 2]:F2} p90 {per[(int)(per.Count * 0.9)]:F2} max {per[^1]:F2} с");
