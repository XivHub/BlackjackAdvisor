using System;

namespace BlackjackAdvisor.Chat
{
    /// <summary>One chat message handed to the parser. Kind is the raw chat-type name, kept only
    /// for the trace — the parser's own decisions run on Sender/Text/IsSpeech/IsRandomRoll, never
    /// on which channel the game tagged the line with. At is the host's own clock reading for the
    /// message (wall time live, the captured timestamp on replay), so a timed safety net inside
    /// the parser never calls DateTime.Now itself and a replayed capture behaves identically every run.</summary>
    public readonly record struct ChatLine(string Kind, string Sender, string Text, bool IsSpeech, bool IsRandomRoll, DateTime At);
}
