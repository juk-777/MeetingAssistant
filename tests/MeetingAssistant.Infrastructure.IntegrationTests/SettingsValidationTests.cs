using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Infrastructure.IntegrationTests.Speech;
using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests;

/// <summary>
/// Настройки проверяются до начала встречи: кривая настройка — отказ с её именем, а не сбой посреди записи.
/// </summary>
public sealed class SettingsValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Storage_WhenMeetingsRootMissing_FailsNamingSetting(string? root)
    {
        using ServiceProvider provider = Build(new Dictionary<string, string?>
        {
            [$"{StorageOptions.Section}:{nameof(StorageOptions.MeetingsRoot)}"] = root,
        });

        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        failure.Message.ShouldContain($"{StorageOptions.Section}:{nameof(StorageOptions.MeetingsRoot)}");
    }

    [Theory]
    [InlineData("", 2, nameof(SpeechOptions.ModelsDirectory))]
    [InlineData(" ", 2, nameof(SpeechOptions.ModelsDirectory))]
    [InlineData(null, 0, nameof(SpeechOptions.RecognizerThreads))]
    [InlineData(null, -1, nameof(SpeechOptions.RecognizerThreads))]
    public void Speech_WhenSettingInvalid_FailsNamingSetting(string? modelsDirectory, int threads, string setting)
    {
        var options = new SpeechOptions { ModelsDirectory = modelsDirectory ?? Models.Directory, RecognizerThreads = threads, RefineThreads = 4 };

        ValidateOptionsResult result = new SpeechOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain($"{SpeechOptions.Section}:{setting}");
    }

    [Fact]
    public void Speech_WhenRefineThreadsInvalid_RecordingStillValid()
    {
        var options = new SpeechOptions { ModelsDirectory = Models.Directory, RecognizerThreads = 2, RefineThreads = 0 };

        ValidateOptionsResult result = new SpeechOptionsValidator().Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Refine_WhenRefineThreadsInvalid_RefusesNamingSetting()
    {
        await using ServiceProvider provider = Build(new Dictionary<string, string?>
        {
            [$"{SpeechOptions.Section}:{nameof(SpeechOptions.ModelsDirectory)}"] = Models.Directory,
            [$"{SpeechOptions.Section}:{nameof(SpeechOptions.RecognizerThreads)}"] = "2",
            [$"{SpeechOptions.Section}:{nameof(SpeechOptions.RefineThreads)}"] = "0",
        });

        Result prepared = await provider.GetRequiredService<IRefineRecognizer>().Prepare(TestContext.Current.CancellationToken);

        prepared.Error.Code.ShouldBe(SpeechErrors.RefineThreadsInvalidCode);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
