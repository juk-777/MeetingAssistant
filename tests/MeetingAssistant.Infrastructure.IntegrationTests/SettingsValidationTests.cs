using MeetingAssistant.Infrastructure.IntegrationTests.Speech;
using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.Infrastructure.Storage;
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
        var options = new SpeechOptions { ModelsDirectory = modelsDirectory ?? Models.Directory, RecognizerThreads = threads };

        ValidateOptionsResult result = new SpeechOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldContain($"{SpeechOptions.Section}:{setting}");
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
