using System.CommandLine;
using System.Globalization;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.Cli.Meetings;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Screen;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Cli.Start;

/// <summary>
/// <c>meeting start</c>: живой созвон — микрофон и системный звук до Ctrl+C.
/// </summary>
internal static class StartCommand
{
    public static Command Create()
    {
        Argument<string> nameArgument = new("name") { Description = "Имя встречи" };

        Option<FileInfo?> glossaryOption = new("--glossary") { Description = "Словарь встречи: шаблон на строку" };

        Option<int?> screenOption = new("--screen")
        {
            Description = "Снимать экран: смены — в screen/ и screen.log; номер монитора с 1 (1 — основной, только Windows)",
            Arity = ArgumentArity.ZeroOrOne,
        };

        Command command = new("start", "Записать встречу: микрофон — «Я», системный звук — «Собеседники»; Ctrl+C — остановить")
        {
            nameArgument,
            glossaryOption,
            screenOption,
        };

        command.SetAction((parseResult, cancellationToken) => Run(
            parseResult.GetRequiredValue(nameArgument),
            parseResult.GetValue(glossaryOption),
            parseResult.GetResult(screenOption) is null ? null : new ScreenRequest(parseResult.GetValue(screenOption)),
            cancellationToken));

        return command;
    }

    private static async Task<int> Run(string name, FileInfo? glossary, ScreenRequest? screen, CancellationToken cancellationToken)
    {
        Result<MeetingHost> opened = MeetingHost.Open();

        if (opened.IsFailure)
        {
            return await MeetingHost.Exit(opened);
        }

        using MeetingHost host = opened.Value;

        Result<string[]> glossaryLines = await MeetingHost.ReadGlossary(glossary, cancellationToken);

        if (glossaryLines.IsFailure)
        {
            return await MeetingHost.Exit(glossaryLines);
        }

        ILiveCapture capture = host.Services.GetRequiredService<ILiveCapture>();
        Result<OpenedCapture> devices = capture.Open();

        if (devices.IsFailure)
        {
            return await MeetingHost.Exit(devices);
        }

        IScreenSource? screenSource = null;

        if (screen is not null)
        {
            Result<IScreenSource> monitor = host.Services.GetRequiredService<IScreenCapture>().Open(screen.Monitor);

            if (monitor.IsFailure)
            {
                return await MeetingHost.Exit(monitor);
            }

            screenSource = monitor.Value;
        }

        Result started = await host.Start(name, glossaryLines.Value, cancellationToken);

        if (started.IsFailure)
        {
            return await MeetingHost.Exit(started);
        }

        Console.WriteLine($"Каталог встречи: {Path.GetDirectoryName(capture.AudioFile)}");
        Console.WriteLine($"Микрофон («Я»): {devices.Value.Microphone}");
        Console.WriteLine($"Системный звук («Собеседники»): {devices.Value.SystemSound}");

        if (screen is not null)
        {
            Console.WriteLine($"Экран: монитор {screen.Monitor?.ToString(CultureInfo.InvariantCulture) ?? "основной"}");
        }

        Console.WriteLine("Идёт запись. Ctrl+C — остановить.");

        return await MeetingHost.Exit(await host.Record(capture.RecordToMeeting(devices.Value.Source), screenSource, cancellationToken));
    }

    /// <summary>
    /// Ключ <c>--screen</c>: экран снимается; номер монитора — если задан.
    /// </summary>
    private sealed record ScreenRequest(int? Monitor);
}
