using System.Collections.Generic;

namespace BlackjackAdvisor.Chat
{
    /// <summary>What the parser needs from its host. Kept free of Dalamud types so Chat/ can be
    /// compiled and exercised outside the plugin, against a stub implementation.</summary>
    public interface IParserHost
    {
        string? LocalPlayerName { get; }
        string ConfiguredDealerName { get; }

        /// <summary>Every player name currently in the party/table, read from the object table on
        /// the framework thread and cached — the checksum learner resolves an abbreviated subject
        /// against this before trusting an equation, since two players who abbreviate the same way
        /// would otherwise merge into one running total. Empty when no roster is available.</summary>
        IReadOnlyList<string> RosterNames { get; }

        /// <summary>Whether the checksum learner may form new hypotheses and bind them. Off does
        /// not affect matching against lines already learned.</summary>
        bool LearnDealerWording { get; }

        void Log(string message);
    }
}
