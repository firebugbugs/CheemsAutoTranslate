namespace CursorTranslator.Services;

public enum AiStreamPhase
{
    WaitingForResponse,
    Thinking,
    Generating,
    Completed
}

public sealed record AiStreamProgress(AiStreamPhase Phase, int? ReasoningTokens = null);
