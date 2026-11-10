using MeetingAssistant.Application;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Application.Meetings.Stop;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Storage;

/// <summary>
/// Сценарии и хранилище в той же регистрации, что в бою, с корнем встреч во временном каталоге.
/// </summary>
internal sealed class StorageHarness : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public StorageHarness()
    {
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StorageOptions.Section}:{nameof(StorageOptions.MeetingsRoot)}"] = Root,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(Time);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
    }

    public string Root { get; } = Directory.CreateTempSubdirectory("meeting-assistant-").FullName;

    public FakeTimeProvider Time { get; } = new(Now);

    public string MeetingFolder => Service<MeetingDirectory>().Path;

    public T Service<T>()
        where T : notnull =>
        _provider.GetRequiredService<T>();

    public async Task<Guid> Start(string name)
    {
        Result<Guid> started = await Service<ICommandHandler<StartMeeting, Guid>>()
            .Handle(new StartMeeting(name, []), TestContext.Current.CancellationToken);

        return started.Value;
    }

    public async Task Stop()
    {
        Result stopped = await Service<ICommandHandler<StopMeeting>>()
            .Handle(new StopMeeting(), TestContext.Current.CancellationToken);

        if (stopped.IsFailure)
        {
            throw new InvalidOperationException(stopped.Error.Code);
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
