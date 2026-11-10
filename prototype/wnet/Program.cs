using System.Diagnostics;
using System.Text.Json;
using Whisper.net;

// args: <segdir> <model> <out> <threads> [prompt]
var segDir = args[0];
var threads = int.Parse(args[3]);
var prompt = args.Length > 4 ? args[4] : null;
var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(segDir, "segments.json"))).RootElement;
var sw = Stopwatch.StartNew();
using var factory = WhisperFactory.FromPath(args[1]);
var builder = factory.CreateBuilder().WithLanguage("ru").WithNoContext().WithThreads(threads);
if (prompt is not null) builder = builder.WithPrompt(prompt);
await using var processor = builder.Build();
var load = sw.Elapsed.TotalSeconds;
var lines = new List<string>();
var per = new List<double>();
double speech = 0;
sw.Restart();
foreach (var m in meta.EnumerateArray())
{
    var i = m.GetProperty("i").GetInt32();
    speech += m.GetProperty("len").GetDouble();
    var one = Stopwatch.StartNew();
    await using var fs = File.OpenRead(Path.Combine(segDir, $"{i:0000}.wav"));
    var text = new List<string>();
    await foreach (var s in processor.ProcessAsync(fs)) text.Add(s.Text.Trim());
    per.Add(one.Elapsed.TotalSeconds);
    lines.Add($"[{m.GetProperty("start").GetDouble(),7:F1}] {string.Join(' ', text)}");
}
var total = sw.Elapsed.TotalSeconds;
per.Sort();
File.WriteAllLines(args[2], lines);
Console.WriteLine($"[turbo t={threads}{(prompt is null ? "" : " prompt")}] загрузка {load:F1} с, распознавание {total:F1} с, на секунду речи {total / speech:F3}, на фразу p50 {per[per.Count / 2]:F2} p90 {per[(int)(per.Count * 0.9)]:F2} max {per[^1]:F2} с");
