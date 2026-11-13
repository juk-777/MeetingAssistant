using MeetingAssistant.Domain.Meetings;
using Shouldly;

namespace MeetingAssistant.Domain.UnitTests.Meetings;

public sealed class WakePolicyTests
{
    private static readonly TimeSpan NoSpeech = TimeSpan.Zero;

    [Fact]
    public void Evaluate_WhenPlainStatement_ReturnsNoReasons()
    {
        WakePolicy.Evaluate("Сегодня обсуждаем план работ", Glossary.Empty, NoSpeech).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Агент, подскажи, где это решение")]
    [InlineData("а что скажет агент")]
    [InlineData("слушай, агент, где это решение")]
    [InlineData("АГЕНТ, проверь")]
    public void Evaluate_WhenAddressWordPresent_ReturnsAddress(string text)
    {
        WakePolicy.Evaluate(text, Glossary.Empty, NoSpeech).ShouldBe([WakeReason.Address]);
    }

    [Fact]
    public void Evaluate_WhenClaudeNamed_ReturnsNoReasons()
    {
        WakePolicy.Evaluate("Клод, подскажи", Glossary.Empty, NoSpeech).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("агентство уже ответило")]
    [InlineData("тут нужен субагент")]
    public void Evaluate_WhenAddressWordIsPartOfAnotherWord_ReturnsNoReasons(string text)
    {
        WakePolicy.Evaluate(text, Glossary.Empty, NoSpeech).ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_WhenQuestionHasMinimumWords_ReturnsQuestion()
    {
        WakePolicy.Evaluate(Question(WakePolicy.MinQuestionWords), Glossary.Empty, NoSpeech)
            .ShouldBe([WakeReason.Question]);
    }

    [Fact]
    public void Evaluate_WhenQuestionIsOneWordShort_ReturnsNoReasons()
    {
        WakePolicy.Evaluate(Question(WakePolicy.MinQuestionWords - 1), Glossary.Empty, NoSpeech).ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_WhenLongPhraseHasNoQuestionMark_ReturnsNoReasons()
    {
        string statement = string.Join(' ', Enumerable.Repeat("слово", WakePolicy.MinQuestionWords + 1));

        WakePolicy.Evaluate(statement, Glossary.Empty, NoSpeech).ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_WhenQuestionWordsAreSeparatedByPunctuation_CountsOnlyWords()
    {
        string question = string.Join(" - ", Enumerable.Repeat("слово", WakePolicy.MinQuestionWords - 1)) + " ?!";

        WakePolicy.Evaluate(question, Glossary.Empty, NoSpeech).ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_WhenTermInDifferentCase_ReturnsTerm()
    {
        Glossary glossary = Glossary.Create(["outbox"]).Value;

        WakePolicy.Evaluate("Тут нужен OutBox для событий", glossary, NoSpeech).ShouldBe([WakeReason.Term]);
    }

    [Fact]
    public void Evaluate_WhenSeveralReasons_ReturnsThemInFixedOrder()
    {
        Glossary glossary = Glossary.Create(["outbox"]).Value;
        string text = "Агент, " + Question(WakePolicy.MinQuestionWords) + " outbox";

        WakePolicy.Evaluate(text, glossary, WakePolicy.AccumulatedSpeechLimit)
            .ShouldBe([WakeReason.Address, WakeReason.Question, WakeReason.Term]);
    }

    [Fact]
    public void Evaluate_WhenAccumulatedSpeechReachesLimit_ReturnsAccumulatedSpeech()
    {
        WakePolicy.Evaluate("Продолжаем обсуждение", Glossary.Empty, WakePolicy.AccumulatedSpeechLimit)
            .ShouldBe([WakeReason.AccumulatedSpeech]);
    }

    [Fact]
    public void Evaluate_WhenAccumulatedSpeechBelowLimit_ReturnsNoReasons()
    {
        TimeSpan belowLimit = WakePolicy.AccumulatedSpeechLimit - TimeSpan.FromMilliseconds(1);

        WakePolicy.Evaluate("Продолжаем обсуждение", Glossary.Empty, belowLimit).ShouldBeEmpty();
    }

    private static string Question(int words) =>
        string.Join(' ', Enumerable.Repeat("слово", words)) + "?";
}
