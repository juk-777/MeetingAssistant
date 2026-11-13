using System.Diagnostics;
using System.Globalization;
using MeetingAssistant.Application.Meetings.Refine;

namespace MeetingAssistant.Cli.Refine;

/// <summary>
/// Ход уточнения в окне: место в записи, прошло, оценка остатка — строкой не чаще раза в <see cref="Every"/>;
/// в конце — итог для замеров (<c>docs/measurements.md</c>).
/// </summary>
internal sealed class ConsoleRefineProgress : IProgress<RefinePosition>
{
    private const double BytesPerMegabyte = 1024 * 1024;

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private TimeSpan _lastPrinted = -Every;

    public RefinePosition? Last { get; private set; }

    public void Report(RefinePosition value)
    {
        Last = value;

        if (value.Elapsed - _lastPrinted < Every)
        {
            return;
        }

        _lastPrinted = value.Elapsed;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[{Clock(value.Position)} из {Clock(value.Duration)}] {Percent(value):F0} %, строк {value.Utterances}, " +
            $"прошло {Clock(value.Elapsed)}, осталось около {Clock(Remaining(value))}"));
    }

    public string Summary()
    {
        using Process process = Process.GetCurrentProcess();
        RefinePosition last = Last ?? new RefinePosition(TimeSpan.Zero, TimeSpan.Zero, 0, TimeSpan.Zero, TimeSpan.Zero);
        double toSpeech = last.Speech > TimeSpan.Zero ? last.Elapsed / last.Speech : 0;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Строк {last.Utterances}; речь {last.Speech.TotalSeconds:F1} с из записи {last.Duration.TotalSeconds:F1} с; " +
            $"распознавание {last.Elapsed.TotalSeconds:F1} с, {toSpeech:F3} к речи; " +
            $"CPU процесса {process.TotalProcessorTime.TotalSeconds:F1} с, пик памяти {process.PeakWorkingSet64 / BytesPerMegabyte:F0} МБ");
    }

    private static double Percent(RefinePosition value) =>
        value.Duration > TimeSpan.Zero ? 100 * value.Position / value.Duration : 100;

    // Остаток — темпом пройденного: тишина между фразами идёт мгновенно, поэтому оценка грубая.
    private static TimeSpan Remaining(RefinePosition value) =>
        value.Position > TimeSpan.Zero ? value.Elapsed * ((value.Duration - value.Position) / value.Position) : TimeSpan.Zero;

    private static string Clock(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}");
}
