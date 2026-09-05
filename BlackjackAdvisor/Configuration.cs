using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using BlackjackAdvisor.Chat;

namespace BlackjackAdvisor
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 2;

        // Dealer rules
        public int DealerStandsOn { get; set; } = 17;       // dealer draws below this total
        public bool DealerHitsSoft17 { get; set; } = true;  // H17 (common); false = S17
        public bool DoubleAfterSplit { get; set; } = true;

        // Whether the host offers these actions at all (excludes them from advice if off)
        public bool HostAllowsDouble { get; set; } = true;
        public bool HostAllowsSplit { get; set; } = true;

        // Chat auto-fill
        public bool AutoFillFromChat { get; set; } = true;
        public bool ChatDebug { get; set; } = false;   // log what the parser extracts

        // Dealer-format learner (see Chat/TemplateStore.cs). Nothing writes to LearnedLines yet.
        public List<LearnedLine> LearnedLines { get; set; } = new();
        public bool LearnDealerWording { get; set; } = true;

        // Dev log: mirrors the parser trace to a local devlog server (see XivHubPluginKit).
        public bool DevLog { get; set; } = false;
        public string DevLogUrl { get; set; } = "";
        public string DealerName { get; set; } = "";    // optional: only accept lines from this sender

        // Chat output
        public string ChatChannel { get; set; } = "/p"; // /p /say /sh /fc /echo ...
        public string SayStand { get; set; } = "stand";
        public string SayHit { get; set; } = "hit";
        public string SayDouble { get; set; } = "double";
        public string SaySplit { get; set; } = "split";

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

        public void Save() => pluginInterface!.SavePluginConfig(this);

        // A config saved by an older version has no LearnedLines at all; one saved by a newer
        // version may carry a role id this build does not know. Neither should crash the load —
        // drop what cannot be understood and keep everything else, so existing settings survive.
        public void Sanitize()
        {
            LearnedLines ??= new List<LearnedLine>();
            var cleaned = new List<LearnedLine>();
            var seen = new HashSet<(string Template, string Dealer)>();
            foreach (var line in LearnedLines)
            {
                if (line == null) continue;
                line.Template ??= "";
                line.Dealer ??= "";
                line.Role ??= "";
                line.Example ??= "";
                if (string.IsNullOrWhiteSpace(line.Template)) continue;
                if (RoleIds.Parse(line.Role) is null) continue;
                if (!seen.Add((line.Template, line.Dealer))) continue;
                cleaned.Add(line);
            }
            LearnedLines = cleaned;
        }
    }
}
