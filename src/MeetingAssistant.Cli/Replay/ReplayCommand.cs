using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using MeetingAssistant.Application;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Cli.Replay;

/// <summary>
/// <c>meeting replay</c>: запись с диска через тот же конвейер, что и живой созвон.
/// </summary>
internal static class ReplayCommand
{
    private const int Success = 0;
    private const int Failure = 1;

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
        // Настройки — рядом с исполняемым файлом, а не в текущем каталоге: meeting запускают откуда угодно.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        ReplayStatistics.Register(builder.Services);

        using IHost host = builder.Build();
        IServiceProvider services = host.Services;

        try
        {
            services.GetRequiredService<IStartupValidator>().Validate();
        }
        catch (OptionsValidationException exception)
        {
            await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, exception.Failures));
            return Failure;
        }

        // Модель грузится до начала встречи: её отсутствие или сбой загрузки не оставляют пустой каталог.
        services.GetRequiredService<ISpeechRecognizer>();

        Result<IAudioSource> source = WavFileAudioSource.Open(wav.FullName, speed, TimeProvider.System);

        if (source.IsFailure)
        {
            return await Fail(source.Error);
        }

        string[] glossaryLines = [];

        if (glossary is not null)
        {
            if (!glossary.Exists)
            {
                await Console.Error.WriteLineAsync($"Нет файла словаря: {glossary.FullName}");
                return Failure;
            }

            glossaryLines = await File.ReadAllLinesAsync(glossary.FullName, cancellationToken);
        }

        Result<Guid> started = await services.GetRequiredService<ICommandHandler<StartMeeting, Guid>>()
            .Handle(new StartMeeting(name ?? Path.GetFileNameWithoutExtension(wav.Name), glossaryLines), cancellationToken);

        if (started.IsFailure)
        {
            return await Fail(started.Error);
        }

        var elapsed = Stopwatch.StartNew();
        Result recorded = await services.GetRequiredService<IMeetingRecorder>().Record(source.Value, cancellationToken);

        if (recorded.IsFailure)
        {
            return await Fail(recorded.Error);
        }

        Meeting meeting = services.GetRequiredService<ICurrentMeeting>().Meeting
            ?? throw new InvalidOperationException("Встреча записана, но не стала текущей.");

        Console.WriteLine(services.GetRequiredService<ReplayStatistics>().Summary(meeting.LastUtteranceNumber, elapsed.Elapsed));

        return Success;
    }

    private static async Task<int> Fail(Error error)
    {
        await Console.Error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{error.Code}: {error.Description}"));
        return Failure;
    }
}
