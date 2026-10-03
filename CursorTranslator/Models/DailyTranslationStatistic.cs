namespace CursorTranslator.Models;

public sealed record DailyTranslationStatistic(
    DateOnly Date,
    long TranslationRequests,
    long TranslationUnits);
