using System.Globalization;
using System.Text.RegularExpressions;
using BlackjackAdvisor.Chat;
using BlackjackAdvisor.Strategy;

// Offline replay of a captured dev-log against the real ChatParser, checked against a sibling
// .expect file. Never referenced by BlackjackAdvisor.csproj — see the README "Parser harness"
// section for how this is run.

bool noBuiltins = false;
bool engineCheck = false;
bool dumpTrace = false;
var fixtures = new List<string>();
foreach (var a in args)
{
    if (a == "--no-builtins") noBuiltins = true;
    else if (a == "--engine-check") engineCheck = true;
    else if (a == "--dump-trace") dumpTrace = true;
    else fixtures.Add(a);
}

int failures = 0;
if (engineCheck) failures += EngineCheck.Run();
foreach (var fixture in fixtures) failures += FixtureRunner.Run(fixture, noBuiltins, dumpTrace);

if (!engineCheck && fixtures.Count == 0)
{
    Console.Error.WriteLine("usage: ParserHarness [--no-builtins] [--engine-check] [--dump-trace] <fixture.log|fixture.expect> [...]");
    return 1;
}

Console.WriteLine(failures == 0 ? "OK" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

// Reads one DevTelemetry capture and its .expect file, replays the chat lines through a real
// ChatParser and checks every #at checkpoint against the resulting state.
static class FixtureRunner
{
    // hh:mm:ss.fff [BlackjackAdvisor] «Kind» [Sender] Text — the captured chat lines. Every other
    // line in the capture (periodic status dumps, the parser's own Dbg trace) does not match and
    // is silently skipped, exactly as a real dev-log replay must skip them.
    private static readonly Regex LineRx = new(
        @"^(\d\d:\d\d:\d\d\.\d\d\d) \[BlackjackAdvisor\] «([^»]*)» \[([^\]]*)\] (.*)$", RegexOptions.Compiled);

    // Mirrors MainWindow's XivChatType allow-list by the chat-type name the capture stores it
    // under — this tool cannot reference Dalamud, so the check runs on the string form instead.
    private static readonly HashSet<string> SpeechKinds = new(StringComparer.Ordinal)
    {
        "Say", "Shout", "TellIncoming", "Party", "Alliance",
        "Ls1", "Ls2", "Ls3", "Ls4", "Ls5", "Ls6", "Ls7", "Ls8",
        "FreeCompany", "NoviceNetwork", "CustomEmote", "StandardEmote", "Yell", "CrossParty",
        "CrossLinkShell1", "CrossLinkShell2", "CrossLinkShell3", "CrossLinkShell4",
        "CrossLinkShell5", "CrossLinkShell6", "CrossLinkShell7", "CrossLinkShell8", "Echo",
    };

    public static int Run(string fixturePath, bool noBuiltins, bool dumpTrace = false)
    {
        // A fixture is normally named for its own capture (venue-lina.log / .expect). A second
        // .expect can replay the SAME capture under a different name (venue-lina.learn.expect)
        // without duplicating the log — point at the .expect directly and name the log it replays
        // with a "#log <relative path>" directive instead of deriving one by extension.
        string expectPath, logPath;
        if (fixturePath.EndsWith(".expect", StringComparison.Ordinal))
        {
            expectPath = fixturePath;
            string? logDirective = File.Exists(expectPath)
                ? File.ReadLines(expectPath).FirstOrDefault(l => l.TrimStart().StartsWith("#log "))
                : null;
            if (logDirective == null) { Console.Error.WriteLine($"FAIL {fixturePath}: no such fixture, or missing '#log' directive"); return 1; }
            logPath = Path.Combine(Path.GetDirectoryName(expectPath) ?? "", logDirective.TrimStart()["#log ".Length..].Trim());
        }
        else
        {
            logPath = fixturePath;
            expectPath = Path.ChangeExtension(logPath, ".expect");
        }
        if (!File.Exists(logPath)) { Console.Error.WriteLine($"FAIL {fixturePath}: no such fixture"); return 1; }
        if (!File.Exists(expectPath)) { Console.Error.WriteLine($"FAIL {fixturePath}: missing {expectPath}"); return 1; }

        var chatLines = new List<(string Time, string Kind, string Sender, string Text)>();
        foreach (var raw in File.ReadLines(logPath))
        {
            var m = LineRx.Match(raw);
            if (m.Success) chatLines.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));
        }

        string? me = null, dealer = null;
        var roster = new List<string>();
        var checks = new List<(string Time, Dictionary<string, string> Fields)>();
        foreach (var raw in File.ReadLines(expectPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') is false) continue;
            if (line.StartsWith("#me ")) me = line["#me ".Length..].Trim();
            else if (line.StartsWith("#dealer ")) dealer = line["#dealer ".Length..].Trim();
            else if (line.StartsWith("#roster ")) roster.Add(line["#roster ".Length..].Trim());
            else if (line.StartsWith("#at "))
            {
                var rest = line["#at ".Length..].Trim();
                int sp = rest.IndexOf(' ');
                string time = sp < 0 ? rest : rest[..sp];
                var fields = new Dictionary<string, string>();
                if (sp >= 0)
                    foreach (var tok in rest[(sp + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = tok.IndexOf('=');
                        if (eq > 0) fields[tok[..eq]] = tok[(eq + 1)..];
                    }
                checks.Add((time, fields));
            }
        }

        if (me == null) { Console.Error.WriteLine($"FAIL {fixturePath}: .expect has no #me directive"); return 1; }

        var trace = new List<string>();
        var host = new HarnessHost
        {
            LocalPlayerName = me,
            ConfiguredDealerName = dealer ?? "",
            RosterNames = roster,
            Trace = trace,
        };
        // --no-builtins disables the attribution regexes below the learned-store lookup, so a
        // fixture can prove learned bindings alone reproduce a venue's hand-filling.
        var parser = new ChatParser(host, noBuiltins);

        int failed = 0, fed = 0;
        foreach (var (time, fields) in checks)
        {
            while (fed < chatLines.Count && string.CompareOrdinal(chatLines[fed].Time, time) <= 0)
            {
                var l = chatLines[fed];
                bool roll = ChatParser.IsRollText(l.Text);
                bool isSpeech = SpeechKinds.Contains(l.Kind);
                var at = DateTime.ParseExact(l.Time, "HH:mm:ss.fff", CultureInfo.InvariantCulture);
                parser.Feed(new ChatLine(l.Kind, l.Sender, l.Text, isSpeech, roll, at));
                fed++;
            }

            var snap = parser.State.Read();
            var actual = new Dictionary<string, string>
            {
                ["hand"] = snap.TotalMode || snap.Hand.Count == 0 ? "-" : string.Join(",", snap.Hand.Select(c => c.Rank)),
                ["up"] = snap.Dealer?.Rank ?? "-",
                ["total"] = snap.TotalMode ? snap.InTotal.ToString(CultureInfo.InvariantCulture) : "-",
                ["mode"] = snap.TotalMode ? "total" : "cards",
                ["split"] = parser.SplitHands ? "true" : "false",
                ["pair"] = snap.InPair ? "true" : "false",
                ["soft"] = snap.InSoft ? "true" : "false",
                ["myTurn"] = snap.MyTurn ? "true" : "false",
                // Multi-word names cannot contain a raw space in a whitespace-tokenized directive.
                ["dealingTo"] = (snap.DealingTo ?? "-").Replace(' ', '_'),
            };

            var mismatches = fields
                .Where(kv => kv.Key != "trace")
                .Where(kv => !actual.TryGetValue(kv.Key, out var v) || v != kv.Value)
                .ToList();
            // trace=some_substring: at least one logged line so far contains "some substring".
            // Checks what the learner has done up to this point without parsing its whole vocabulary.
            if (fields.TryGetValue("trace", out var wantSubstring))
            {
                string needle = wantSubstring.Replace('_', ' ');
                if (!trace.Any(t => t.Contains(needle, StringComparison.Ordinal)))
                    mismatches.Add(new KeyValuePair<string, string>("trace", wantSubstring));
            }

            bool ok = mismatches.Count == 0;
            string fieldStr = string.Join(' ', fields.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {Path.GetFileName(fixturePath)} @{time} {fieldStr}");
            if (!ok)
            {
                failed++;
                foreach (var kv in mismatches)
                    Console.WriteLine(kv.Key == "trace"
                        ? $"     expected trace to contain '{kv.Value.Replace('_', ' ')}', it never did"
                        : $"     expected {kv.Key}={kv.Value}, got {kv.Key}={actual[kv.Key]}");
            }
        }

        failed += CheckBindInvariant(fixturePath, trace);
        if (dumpTrace) foreach (var t in trace) Console.Error.WriteLine($"TRACE {Path.GetFileName(fixturePath)}: {t}");
        return failed;
    }

    // No "bind <role> '<template>'" line may appear in the trace without a preceding
    // "hyp confirm (2/2) <role> '<template>'" for that same (role, template) pair — mechanically
    // checked here so the invariant cannot silently regress.
    private static readonly Regex BindLineRx = new(@"^bind (\S+) '(.+)'$", RegexOptions.Compiled);
    private static readonly Regex ConfirmTwoLineRx = new(@"^hyp confirm \(2/2\) (\S+) '(.+)'$", RegexOptions.Compiled);

    private static int CheckBindInvariant(string fixturePath, List<string> trace)
    {
        int failed = 0;
        for (int i = 0; i < trace.Count; i++)
        {
            var bind = BindLineRx.Match(trace[i]);
            if (!bind.Success) continue;
            string role = bind.Groups[1].Value, template = bind.Groups[2].Value;
            bool confirmed = trace.Take(i).Any(t =>
            {
                var m = ConfirmTwoLineRx.Match(t);
                return m.Success && m.Groups[1].Value == role && m.Groups[2].Value == template;
            });
            Console.WriteLine($"{(confirmed ? "PASS" : "FAIL")} {Path.GetFileName(fixturePath)} bind-invariant {role} '{template}'");
            if (!confirmed) failed++;
        }
        return failed;
    }

    private sealed class HarnessHost : IParserHost
    {
        public string? LocalPlayerName { get; set; }
        public string ConfiguredDealerName { get; set; } = "";
        public IReadOnlyList<string> RosterNames { get; set; } = Array.Empty<string>();
        public bool LearnDealerWording { get; set; } = true;
        public List<string> Trace { get; set; } = new();
        public void Log(string message) => Trace.Add(message);
    }
}

// The default rules (dealer stands on 17, hits soft 17) must keep reproducing the infinite-deck
// H17 dealer bust probability by up card. Expected values are that distribution computed
// independently by the same recursive method BlackjackEngine uses (P(A)=P(2..9)=1/13,
// P(ten-value)=4/13, dealer draws to 17 and hits a soft 17), rounded to 3 decimals. This guards
// the engine against a refactor drifting off the validated math.
static class EngineCheck
{
    public static int Run()
    {
        var engine = new BlackjackEngine(dealerHitsSoft17: true, doubleAfterSplit: true, dealerStandsOn: 17);
        (int Up, double Bust)[] expected =
        {
            (2, 0.357), (3, 0.377), (4, 0.397), (5, 0.418), (6, 0.439),
            (7, 0.262), (8, 0.245), (9, 0.228), (10, 0.212), (1, 0.139),
        };

        int failed = 0;
        foreach (var (up, want) in expected)
        {
            double got = Math.Round(engine.DealerDistributionFromUp(up)[0], 3, MidpointRounding.AwayFromZero);
            bool ok = Math.Abs(got - want) < 0.0005;
            string label = up == 1 ? "A" : up.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} engine-check bust(up={label})={got:F3} want={want:F3}");
            if (!ok) failed++;
        }
        return failed;
    }
}
