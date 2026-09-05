namespace BlackjackAdvisor.Chat
{
    /// <summary>One chat message handed to the parser. Kind is the raw chat-type name, kept only
    /// for the trace — the parser's own decisions run on Sender/Text/IsSpeech/IsRandomRoll, never
    /// on which channel the game tagged the line with.</summary>
    public readonly record struct ChatLine(string Kind, string Sender, string Text, bool IsSpeech, bool IsRandomRoll);
}
