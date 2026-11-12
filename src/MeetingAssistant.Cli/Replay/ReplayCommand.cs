using System.CommandLine;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Cli.Meetings;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Cli.Replay;

/// <summary>
/// <c>meeting replay</c>: запись с диска через тот же конвейер, что и живой созвон.
/// </summary>
internal static class ReplayCommand
{
    public static Command Create()
    {
        Argument<FileInfo> wavArgument = new("wav")
        {
            Description = "Запись: 16 кГц, моно («Собеседники») или стерео (левый — «Я», правый — «Собеседники»)",
        };

        Option<string?> nameOption = new("--name") { Description = "Имя встречи, по умолчанию — имя файла" };

        Option<FileInfo?> glossaryOption = new("--glossary") { Description = "Словарь встречи: шаблон на строку" };

        Option<double> speedOption = new("--speed")
        {
            Description = "Во сколько раз быстрее реального темпа",
            DefaultValueFactory = _ => 1,
        };
        speedOption.Validators.Add(result =>
        {
            double speed = result.GetValueOrDefault<double>();

            if (!double.IsFinite(speed) || speed <= 0)
            {
                result.AddError("--speed должен быть конечным числом больше нуля");
            }
        });

        Command command = new("replay", "Прогнать запись с диска: транскрипт и события в каталог встречи")
        {
            wavArgument,
            nameOption,
            glossaryOption,
            speedOption,
        };

        command.SetAction((parseResult, cancellationToken) => Run(
            parseResult.GetRequiredValue(wavArgument),
            parseResult.GetValue(nameOption),
            parseResult.GetValue(glossaryOption),
            parseResult.GetValue(speedOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> Run(
        FileInfo wav,
        string? name,
        FileInfo? glossary,
        double speed,
        CancellationToken cancellationToken)
    {
        Result<MeetingHost> opened = MeetingHost.Open();

        if (opened.IsFailure)
        {
            return await MeetingHost.Exit(opened);
        }

        using MeetingHost host = opened.Value;

        Result<IAudioSource> source = WavFileAudioSource.Open(wav.FullName, speed, TimeProvider.System);

        if (source.IsFailure)
        {
            return await MeetingHost.Exit(source);
        }

        Result<string[]> glossaryLines = await MeetingHost.ReadGlossary(glossary, cancellationToken);

        if (glossaryLines.IsFailure)
        {
            return await MeetingHost.Exit(glossaryLines);
        }

        Result started = await host.Start(name ?? Path.GetFileNameWithoutExtension(wav.Name), glossaryLines.Value, cancellationToken);

        if (started.IsFailure)
        {
            return await MeetingHost.Exit(started);
        }

        return await MeetingHost.Exit(await host.Record(source.Value, cancellationToken));
    }
}
