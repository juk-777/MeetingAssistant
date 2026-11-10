using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Shouldly;

namespace MeetingAssistant.Domain.UnitTests.Meetings;

public sealed class SpeechSpanTests
{
    [Fact]
    public void Constructor_WhenStartIsNegative_ThrowsInvalidSpeechSpan()
    {
        DomainException exception = Should.Throw<DomainException>(
            () => new SpeechSpan(TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1)));

        exception.Code.ShouldBe(MeetingErrors.InvalidSpeechSpan);
    }

    [Fact]
    public void Constructor_WhenDurationIsZero_ThrowsInvalidSpeechSpan()
    {
        DomainException exception = Should.Throw<DomainException>(
            () => new SpeechSpan(TimeSpan.Zero, TimeSpan.Zero));

        exception.Code.ShouldBe(MeetingErrors.InvalidSpeechSpan);
    }

    [Fact]
    public void Constructor_WhenStartIsZeroAndDurationPositive_Succeeds()
    {
        var span = new SpeechSpan(TimeSpan.Zero, TimeSpan.FromTicks(1));

        span.Start.ShouldBe(TimeSpan.Zero);
        span.Duration.ShouldBe(TimeSpan.FromTicks(1));
    }
}
