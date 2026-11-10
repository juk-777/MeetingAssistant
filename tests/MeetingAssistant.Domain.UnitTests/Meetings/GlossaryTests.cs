using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Shouldly;

namespace MeetingAssistant.Domain.UnitTests.Meetings;

public sealed class GlossaryTests
{
    [Fact]
    public void Create_WhenLinesHaveBlanks_SkipsThem()
    {
        Result<Glossary> result = Glossary.Create(["outbox", "", "   ", "сага"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
    }

    [Fact]
    public void Create_WhenPatternIsInvalid_FailsWithInvalidPatternAndLineNumber()
    {
        Result<Glossary> result = Glossary.Create(["outbox", "", "(незакрытая"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(Glossary.InvalidPatternCode);
        result.Error.Description.ShouldContain("3");
    }

    [Fact]
    public void Matches_WhenPatternIsRegex_MatchesByPattern()
    {
        Glossary glossary = Glossary.Create([@"саг[аи]"]).Value;

        glossary.Matches("обсуждаем саги заказа").ShouldBeTrue();
        glossary.Matches("обсуждаем заказ").ShouldBeFalse();
    }

    [Fact]
    public void Matches_WhenGlossaryIsEmpty_ReturnsFalse()
    {
        Glossary.Empty.Matches("любой текст").ShouldBeFalse();
    }

    [Fact]
    public void Matches_WhenPatternTimesOut_TreatsAsNoMatchAndChecksOtherTerms()
    {
        Glossary glossary = Glossary.Create([@"^(a+)+$", "outbox"]).Value;
        string catastrophic = new string('a', 64) + "!";

        glossary.Matches(catastrophic).ShouldBeFalse();
        glossary.Matches(catastrophic + " outbox").ShouldBeTrue();
    }
}
