namespace BlackjackAdvisor.Chat
{
    /// <summary>What a matched dealer line means once its template is bound. Mirrors the roles a
    /// human would assign by hand: whose cards are coming, whose turn it is, or that the line
    /// carries no signal at all.</summary>
    public enum TemplateRole
    {
        Ignore,
        DealTo,
        DealerFirst,
        DealerNext,
        Acting,
        EndTurn,
        Total,
        RoundStart,
    }

    /// <summary>One binding of a wording template to a role, either confirmed by the checksum
    /// learner or entered by hand. Dealer == "" scopes the line to every dealer.</summary>
    public sealed class LearnedLine
    {
        public string Template { get; set; } = "";
        public string Role { get; set; } = "";
        public string Dealer { get; set; } = "";
        public string Example { get; set; } = "";
        public bool Auto { get; set; }
        public int Hits { get; set; }
        public long LearnedAtUnix { get; set; }
    }

    /// <summary>The stable string form of a <see cref="TemplateRole"/>, persisted instead of the
    /// numeric enum value so a config written by a future version with a role this build does not
    /// know degrades to Ignore rather than throwing on load.</summary>
    public static class RoleIds
    {
        public static TemplateRole? Parse(string id) => id switch
        {
            "deal-to" => TemplateRole.DealTo,
            "dealer-first" => TemplateRole.DealerFirst,
            "dealer-next" => TemplateRole.DealerNext,
            "acting" => TemplateRole.Acting,
            "end-turn" => TemplateRole.EndTurn,
            "total" => TemplateRole.Total,
            "round-start" => TemplateRole.RoundStart,
            "ignore" => TemplateRole.Ignore,
            _ => null,
        };

        public static string Id(TemplateRole role) => role switch
        {
            TemplateRole.DealTo => "deal-to",
            TemplateRole.DealerFirst => "dealer-first",
            TemplateRole.DealerNext => "dealer-next",
            TemplateRole.Acting => "acting",
            TemplateRole.EndTurn => "end-turn",
            TemplateRole.Total => "total",
            TemplateRole.RoundStart => "round-start",
            TemplateRole.Ignore => "ignore",
            _ => "ignore",
        };

        public static string Label(TemplateRole role) => role switch
        {
            TemplateRole.DealTo => "Deals opening cards",
            TemplateRole.DealerFirst => "Dealer's first card",
            TemplateRole.DealerNext => "Dealer draws again",
            TemplateRole.Acting => "Player takes a card",
            TemplateRole.EndTurn => "Player's turn ends",
            TemplateRole.Total => "Announces a total",
            TemplateRole.RoundStart => "Starts a new round",
            TemplateRole.Ignore => "Ignore this line",
            _ => "Ignore this line",
        };
    }
}
