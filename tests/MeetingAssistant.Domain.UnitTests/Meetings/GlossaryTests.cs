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
    public async Task Matches_WhenPatternBacktracksCatastrophically_AnswersWithoutHanging()
    {
        // На движке с возвратом этот вход не укладывается ни в какой разумный срок: предел роняет тест, а не вешает прогон.
        Glossary glossary = Glossary.Create([@"^(a+)+$"]).Value;
        string catastrophic = new string('a', 5000) + "!";

        bool matched = await Task.Run(() => glossary.Matches(catastrophic), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        matched.ShouldBeFalse();
        glossary.Matches(new string('a', 5000)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"(сага)\s+\1")]
    [InlineData(@"оплат(?=а)")]
    [InlineData(@"(?<!пред)оплата")]
    [InlineData(@"(?>сага)")]
    [InlineData(@"(?(сага)заказ|оплата)")]
    [InlineData(@"a{100000}")]
    public void Create_WhenPatternNeedsBacktracking_FailsWithInvalidPatternAndLineNumber(string pattern)
    {
        Result<Glossary> result = Glossary.Create(["outbox", pattern]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(Glossary.InvalidPatternCode);
        result.Error.Description.ShouldContain("2");
    }
}
