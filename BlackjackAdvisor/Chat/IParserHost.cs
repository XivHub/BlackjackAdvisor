namespace BlackjackAdvisor.Chat
{
    /// <summary>What the parser needs from its host. Kept free of Dalamud types so Chat/ can be
    /// compiled and exercised outside the plugin, against a stub implementation.</summary>
    public interface IParserHost
    {
        string? LocalPlayerName { get; }
        string ConfiguredDealerName { get; }
        void Log(string message);
    }
}
