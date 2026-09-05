using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using BlackjackAdvisor.Chat;
using BlackjackAdvisor.Strategy;
using XivHubPluginKit.UI;

namespace BlackjackAdvisor.Windows
{
    public class MainWindow : Window, IDisposable, IParserHost
    {
        private readonly Configuration c;
        private readonly ChatParser parser;

        // Cached on the framework thread (the name-collision guard needs it on the chat
        // thread), read on whichever thread asks — a reference swap is all that needs to be atomic.
        private volatile IReadOnlyList<string> rosterCache = Array.Empty<string>();

        private BlackjackEngine? engine;
        private bool lastH17, lastDas;
        private int lastStandOn;

        // Gil ledger
        private int sessionStartGil = -1;
        private int wins, losses, pushes;

        // "Learned dealer lines" (Rules): the dealer combo's own filter, and the pending
        // "forget everything for this dealer" confirm modal (opened next frame — see DrawForgetModal).
        private string? learnedDealerFilter;
        private bool learnedForgetOpen;
        private string learnedForgetDealer = "";

        // The role combo's fixed display order: the plan's copy lists the four checksum roles
        // before the housekeeping ones, not the enum's own declaration order (which puts Ignore first).
        private static readonly TemplateRole[] RoleOrder =
        {
            TemplateRole.DealTo, TemplateRole.DealerFirst, TemplateRole.DealerNext, TemplateRole.Acting,
            TemplateRole.EndTurn, TemplateRole.Total, TemplateRole.RoundStart, TemplateRole.Ignore,
        };
        private static readonly string[] RoleLabels = RoleOrder.Select(RoleIds.Label).ToArray();

        // Crisp large font (baked once, downscaled when drawn -> sharp).
        private readonly IFontHandle bigFont;

        // The card table. Baize, card stock and suit pips are the real-world
        // objects the window depicts, not application chrome, so they are defined
        // here and stay put; see XivHubPluginKit/UI/THEME.md.
        private static readonly Vector4 Felt = new(0.09f, 0.28f, 0.17f, 1f);
        private static readonly Vector4 FeltRim = new(0.04f, 0.15f, 0.09f, 1f);
        private static readonly Vector4 FeltText = new(0.72f, 0.86f, 0.74f, 1f);
        private static readonly Vector4 CardFace = new(0.97f, 0.97f, 0.95f, 1f);
        private static readonly Vector4 CardEdge = new(0.14f, 0.14f, 0.16f, 1f);
        private static readonly Vector4 SuitRed = new(0.78f, 0.13f, 0.13f, 1f);
        private static readonly Vector4 SuitBlack = new(0.10f, 0.10f, 0.12f, 1f);
        private static readonly Vector4 Pill = new(0.18f, 0.18f, 0.22f, 1f);
        private static readonly Vector4 White = new(1f, 1f, 1f, 1f);

        public MainWindow(Configuration cfg) : base("Blackjack Advisor")
        {
            c = cfg;
            parser = new ChatParser(this);
            // Config is sanitized before this window exists (see Plugin.cs); loading here means a
            // config saved by an older version, or one with a role id this build no longer knows,
            // never reaches the store at all.
            foreach (var line in c.LearnedLines) parser.Store.Add(line);
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(400, 440),
                MaximumSize = new Vector2(900, 1400),
            };
            bigFont = Plugin.PluginInterface.UiBuilder.FontAtlas
                .NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(34f)));
            Plugin.ChatGui.ChatMessage += OnChatMessage;
            Plugin.Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Plugin.ChatGui.ChatMessage -= OnChatMessage;
            Plugin.Framework.Update -= OnFrameworkUpdate;
            bigFont.Dispose();
        }

        // Refreshes the roster from the object table on the framework thread, once a frame — the
        // learner reads the cached snapshot from the chat thread via IParserHost.RosterNames.
        private void OnFrameworkUpdate(IFramework framework)
        {
            // Who is standing at a card table changes at human speed, so reading the object table
            // once a second spares a crowded district several hundred string allocations a frame.
            var now = DateTime.UtcNow;
            if (now - lastRosterRead >= TimeSpan.FromSeconds(1))
            {
                lastRosterRead = now;
                rosterCache = Plugin.ObjectTable.PlayerObjects.Select(p => p.Name.TextValue).ToArray();
            }

            // The learner runs on the chat thread and the config must be written from this one.
            // Saving here rather than in Draw means a line learned while the window was closed
            // still survives a restart.
            if (!parser.LearnedDirty) return;
            c.LearnedLines = parser.Store.All().ToList();
            c.Save();
            parser.ClearLearnedDirty();
        }

        private DateTime lastRosterRead = DateTime.MinValue;

        // ---- IParserHost ---------------------------------------------------------------

        public string? LocalPlayerName => Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
        public string ConfiguredDealerName => c.DealerName;
        public IReadOnlyList<string> RosterNames => rosterCache;
        public bool LearnDealerWording => c.LearnDealerWording;

        // The parser trace goes to the dev log whenever one is configured, and to game chat only
        // when the user asked to see it there.
        public void Log(string message)
        {
            Plugin.Telemetry.Log(message);
            if (c.ChatDebug) Plugin.ChatGui.Print($"[BJ] {message}");
        }

        private BlackjackEngine Engine()
        {
            if (engine == null || lastH17 != c.DealerHitsSoft17 || lastDas != c.DoubleAfterSplit
                || lastStandOn != c.DealerStandsOn)
            {
                engine = new BlackjackEngine(c.DealerHitsSoft17, c.DoubleAfterSplit, c.DealerStandsOn);
                lastH17 = c.DealerHitsSoft17;
                lastDas = c.DoubleAfterSplit;
                lastStandOn = c.DealerStandsOn;
            }
            return engine;
        }

        public override void Draw()
        {
            var snap = parser.State.Read();
            bool ready = snap.Dealer.HasValue && (snap.TotalMode || snap.Hand.Count > 0);
            EvalResult? r = ready ? Evaluate(snap) : null;

            Plugin.Telemetry.Snapshot(() =>
                $"you={(snap.TotalMode ? $"total {snap.InTotal}{(snap.InSoft ? " soft" : "")}" : snap.Hand.Count == 0 ? "-" : string.Concat(snap.Hand.Select(h => h.Rank)))}"
                + $" up={snap.Dealer?.Rank ?? "-"} dealingTo={snap.DealingTo ?? "-"} myTurn={snap.MyTurn}"
                + $" filled={snap.FilledFromChat} best={(r?.HasBest == true ? r.Best.ToString() : "-")}");

            DrawRules();
            ImGui.Spacing();
            DrawTeachBanner();
            DrawTable(snap, r);
            DrawLearnedNote();
            ImGui.Spacing();
            if (parser.Hypotheses.ValuesLookWrong)
            {
                ImGui.TextColored(HubStyle.Warn, "This table's totals don't match the card values I know.");
                ImGui.TextColored(HubStyle.Muted, "Enter your hand by hand and check the host's rules.");
            }
            else
            {
                DrawRecommendation(r, snap);
            }
            ImGui.Separator();
            DrawControls();
            ImGui.Separator();
            DrawLedger();
            ImGui.Separator();
            DrawAppearance();
        }

        private EvalResult Evaluate(HandState.Snapshot snap) => snap.TotalMode
            ? Engine().EvaluateTotal(snap.InTotal, snap.InSoft, snap.InPair, snap.Dealer!.Value.Value, c.HostAllowsDouble, c.HostAllowsSplit)
            : Engine().Evaluate(snap.Hand.Select(h => h.Value).ToList(), snap.Dealer!.Value.Value, c.HostAllowsDouble, c.HostAllowsSplit);

        // ---- Crisp text (downscale from the baked 34px font) -------------------------

        private void TextCrisp(ImDrawListPtr dl, float size, Vector2 pos, uint col, string text)
        {
            if (bigFont is { Available: true })
                using (bigFont.Push())
                    dl.AddText(ImGui.GetFont(), size, pos, col, text);
            else
                dl.AddText(ImGui.GetFont(), size, pos, col, text);
        }

        private Vector2 MeasureCrisp(float size, string text)
        {
            if (bigFont is { Available: true })
                using (bigFont.Push())
                    return ImGui.CalcTextSize(text) * (size / ImGui.GetFontSize());
            return ImGui.CalcTextSize(text) * (size / ImGui.GetFontSize());
        }

        // ---- Teach banner --------------------------------------------------------------

        // One banner slot for the whole learner-teaching flow, drawn between the rules header and
        // the felt table: an active candidate proposal takes priority, then a just-taught undo
        // window, then the "no other line to try" dead end, then the plain unclaimed-rolls notice.
        // At most one of these is ever true at once, and none of them draw anything when idle.
        private void DrawTeachBanner()
        {
            if (parser.PendingTeach is { } cand) { DrawTeachCandidate(cand); return; }
            if (parser.CanUndoTeach) { DrawTeachUndo(); return; }
            if (parser.TeachExhausted) { DrawTeachExhausted(); return; }
            if (parser.PendingUnclaimedRanks is { } ranks) DrawUnclaimedRolls(ranks);
        }

        private void DrawUnclaimedRolls(IReadOnlyList<string> ranks)
        {
            ImGui.TextColored(HubStyle.Warn, $"Unclaimed cards: {string.Join(", ", ranks)}");
            ImGui.TextColored(HubStyle.Muted, "This dealer announces no totals, so I can't tell whose these are.");
            if (ImGui.Button("Those were my cards")) parser.ClaimUnclaimedRolls();
            ImGui.SameLine();
            if (ImGui.Button("Not mine")) parser.DismissUnclaimedRolls();
            ImGui.Spacing();
        }

        private static string TeachYesLabel(TemplateRole role) => role switch
        {
            TemplateRole.DealTo => "Yes, it deals opening cards",
            TemplateRole.DealerFirst or TemplateRole.DealerNext => "Yes, that's the dealer's own card",
            TemplateRole.Acting => "Yes, that's a player taking a card",
            _ => "Yes",
        };

        private void DrawTeachCandidate(ChatParser.TeachCandidate cand)
        {
            ImGui.TextColored(HubStyle.Warn, "Learn this dealer's wording?");
            using (ImRaii.Child("##teachRawLine", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() + 8), true))
                ImGui.TextWrapped(cand.RawLine);
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(HubStyle.Muted,
                "That line came right before your cards. If it always announces a player's opening cards, "
                + "your hand will fill in by itself from now on.");
            ImGui.PopTextWrapPos();

            // Not Primary(): the window's one gold action is the recommended-move send button.
            if (ImGui.Button(TeachYesLabel(cand.Role))) parser.AcceptTeach();
            ImGui.SameLine();
            if (ImGui.Button("No, not that line")) parser.RejectTeachCandidate();
            ImGui.SameLine();
            if (ImGui.Button("Not now")) parser.DismissTeach();
            ImGui.Spacing();
        }

        private void DrawTeachExhausted()
        {
            ImGui.TextColored(HubStyle.Muted, "No other line to try. Keep entering cards as usual.");
            ImGui.SameLine();
            if (ImGui.Button("Got it")) parser.DismissTeach();
            ImGui.Spacing();
        }

        private void DrawTeachUndo()
        {
            ImGui.TextColored(HubStyle.Good, "Learned.");
            ImGui.SameLine();
            if (ImGui.Button("Undo")) parser.UndoTeach();
            ImGui.Spacing();
        }

        // The quiet note under the felt table once something was bound this session — most players
        // will never open Rules on their own, so this is the only place that says the learner did
        // anything at all.
        private void DrawLearnedNote()
        {
            int learned = parser.Hypotheses.SessionBindCount(parser.DealerScope);
            if (learned == 0) return;
            ImGui.TextColored(HubStyle.Info, $"· learned {learned} of this dealer's lines");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open Rules → Learned dealer lines to see or change them.");
        }

        // ---- The felt table ----------------------------------------------------------

        private void DrawTable(HandState.Snapshot snap, EvalResult? r)
        {
            var dl = ImGui.GetWindowDrawList();
            var start = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            var cardSize = new Vector2(50, 72);
            // Size the felt to its contents so a tall label font never pushes the bottom card past the edge.
            float lineH = ImGui.GetTextLineHeightWithSpacing();
            float h = 10 + lineH + cardSize.Y + 6 + lineH + cardSize.Y + 12;
            dl.AddRectFilled(start, start + new Vector2(w, h), ImGui.GetColorU32(Felt), 8f);
            dl.AddRect(start, start + new Vector2(w, h), ImGui.GetColorU32(FeltRim), 8f, ImDrawFlags.None, 1.5f);
            ImGui.SetCursorScreenPos(start + new Vector2(14, 10));
            ImGui.BeginGroup();

            ImGui.TextColored(FeltText, "DEALER");
            if (snap.Dealer.HasValue) CardWidget(snap.Dealer.Value.Rank, snap.Dealer.Value.Suit, false, cardSize);
            else CardWidget("", '?', true, cardSize);

            ImGui.Dummy(new Vector2(0, 6));

            ImGui.TextColored(FeltText, "YOU");
            if (parser.SplitHands)
            {
                ImGui.SameLine();
                ImGui.TextColored(HubStyle.Warn, "· split — enter each hand yourself");
            }
            else if (snap.FilledFromChat)
            {
                ImGui.SameLine();
                ImGui.TextColored(HubStyle.Info, "· auto-filled from chat");
            }

            if (snap.TotalMode)
            {
                DrawPill($"Total {snap.InTotal}{(snap.InSoft ? " soft" : "")}{(snap.InPair ? " pair" : "")}", Pill, CardFace, 20f);
            }
            else if (snap.Hand.Count == 0)
            {
                CardWidget("", '?', true, cardSize);
            }
            else
            {
                for (int i = 0; i < snap.Hand.Count; i++)
                {
                    if (i > 0) ImGui.SameLine(0, 6);
                    CardWidget(snap.Hand[i].Rank, snap.Hand[i].Suit, false, cardSize);
                }
                if (r != null)
                {
                    ImGui.SameLine(0, 12);
                    var cp = ImGui.GetCursorScreenPos();
                    ImGui.SetCursorScreenPos(new Vector2(cp.X, cp.Y + 24));
                    if (r.Bust) DrawPill($"{r.Total} BUST", new Vector4(0.45f, 0.14f, 0.14f, 1f), White, 18f);
                    else DrawPill($"{r.Total}{(r.Soft ? " soft" : "")}{(r.IsPair ? " pair" : "")}", Pill, CardFace, 18f);
                }
            }

            ImGui.EndGroup();
            ImGui.SetCursorScreenPos(start + new Vector2(0, h + 4));
        }

        private void CardWidget(string rank, char suit, bool faceDown, Vector2 size)
        {
            var pos = ImGui.GetCursorScreenPos();
            DrawCard(pos, size, rank, suit, faceDown);
            ImGui.Dummy(size);
        }

        private void DrawCard(Vector2 pos, Vector2 size, string rank, char suit, bool faceDown)
        {
            var dl = ImGui.GetWindowDrawList();
            var max = pos + size;
            if (faceDown)
            {
                dl.AddRectFilled(pos, max, ImGui.GetColorU32(new Vector4(0.22f, 0.30f, 0.48f, 1f)), 6f);
                dl.AddRect(pos + new Vector2(4, 4), max - new Vector2(4, 4),
                    ImGui.GetColorU32(new Vector4(0.48f, 0.58f, 0.82f, 1f)), 4f, ImDrawFlags.None, 1.5f);
                dl.AddRect(pos, max, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.10f, 1f)), 6f, ImDrawFlags.None, 1.5f);
                return;
            }

            dl.AddRectFilled(pos, max, ImGui.GetColorU32(CardFace), 6f);
            dl.AddRect(pos, max, ImGui.GetColorU32(CardEdge), 6f, ImDrawFlags.None, 1.5f);

            uint col = ImGui.GetColorU32(suit is '♥' or '♦' ? SuitRed : SuitBlack);
            string pip = suit.ToString();
            float cornerSz = 16f;
            float pipSz = 30f;

            TextCrisp(dl, cornerSz, pos + new Vector2(5, 3), col, rank);
            var pipDim = MeasureCrisp(pipSz, pip);
            TextCrisp(dl, pipSz, pos + (size - pipDim) * 0.5f, col, pip);
            var brDim = MeasureCrisp(cornerSz, rank);
            TextCrisp(dl, cornerSz, max - brDim - new Vector2(5, 3), col, rank);
        }

        private void DrawPill(string text, Vector4 bg, Vector4 fg, float fontSize)
        {
            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var pad = new Vector2(10, 5);
            var ts = MeasureCrisp(fontSize, text);
            var size = ts + pad * 2;
            dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(bg), size.Y * 0.5f);
            TextCrisp(dl, fontSize, pos + pad, ImGui.GetColorU32(fg), text);
            ImGui.Dummy(size);
        }

        // ---- Recommendation ----------------------------------------------------------

        private void DrawRecommendation(EvalResult? r, HandState.Snapshot snap)
        {
            if (r == null)
            {
                ImGui.TextColored(HubStyle.Muted, "Pick the dealer's up card and your cards below.");
                return;
            }

            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            const float bh = 38;

            string label;
            Vector4 col;
            if (r.Blackjack) { label = "BLACKJACK!"; col = HubStyle.Accent; }
            else if (r.Bust) { label = "BUST"; col = HubStyle.Bad; }
            else if (r.HasBest) { label = MoveLabel(r.Best).ToUpperInvariant(); col = MoveColor(r.Best); }
            else { label = "-"; col = HubStyle.Muted; }

            dl.AddRectFilled(pos, pos + new Vector2(w, bh), ImGui.GetColorU32(col * new Vector4(0.42f, 0.42f, 0.42f, 1f)), 6f);
            dl.AddRect(pos, pos + new Vector2(w, bh), ImGui.GetColorU32(col), 6f, ImDrawFlags.None, 1.5f);

            float sz = 21;
            var td = MeasureCrisp(sz, label);
            TextCrisp(dl, sz, pos + new Vector2((w - td.X) * 0.5f, (bh - td.Y) * 0.5f), ImGui.GetColorU32(White), label);

            if (!r.Bust && !r.Blackjack && r.HasBest)
            {
                double ev = r.Options.First(o => o.Move == r.Best).EV;
                string evs = FormatEV(ev);
                var ed = MeasureCrisp(14, evs);
                TextCrisp(dl, 14, pos + new Vector2(w - ed.X - 10, (bh - ed.Y) * 0.5f), ImGui.GetColorU32(White), evs);
            }
            ImGui.Dummy(new Vector2(w, bh));

            if (r.Bust) return;

            if (r.HasBest)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(HubStyle.Muted, Explain(r, snap));
                ImGui.PopTextWrapPos();
            }

            ImGui.Dummy(new Vector2(0, 2));
            foreach (var o in r.Options)
                if (o.Available)
                    EVBar(r, o);

            // Manual chat send (never automatic)
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.TextColored(HubStyle.Muted, $"Say in {c.ChatChannel} (click to send):");
            SendButton(Move.Stand, r);
            ImGui.SameLine(); SendButton(Move.Hit, r);
            if (OptAvailable(r, Move.Double)) { ImGui.SameLine(); SendButton(Move.Double, r); }
            if (OptAvailable(r, Move.Split)) { ImGui.SameLine(); SendButton(Move.Split, r); }
        }

        private void EVBar(EvalResult r, OptionEV o)
        {
            bool best = r.HasBest && o.Move == r.Best;
            var dl = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            float full = ImGui.GetContentRegionAvail().X;
            const float labelW = 92, valW = 56, gap = 8;
            float barW = Math.Max(40, full - labelW - valW - gap * 2);
            float barH = ImGui.GetFontSize();

            uint txt = ImGui.GetColorU32(best ? HubStyle.Good : HubStyle.Muted);
            dl.AddText(p0, txt, MoveLabel(o.Move));

            var barPos = new Vector2(p0.X + labelW, p0.Y);
            dl.AddRectFilled(barPos, barPos + new Vector2(barW, barH), ImGui.GetColorU32(HubColors.Get("HubFrameBg")), 3f);
            float center = barPos.X + barW * 0.5f;
            float t = Math.Clamp((float)o.EV, -1.5f, 1.5f) / 1.5f;
            uint fill = ImGui.GetColorU32(o.EV >= 0 ? new Vector4(0.30f, 0.70f, 0.35f, 1f) : new Vector4(0.80f, 0.32f, 0.32f, 1f));
            if (t >= 0)
                dl.AddRectFilled(new Vector2(center, barPos.Y), new Vector2(center + t * barW * 0.5f, barPos.Y + barH), fill, 3f);
            else
                dl.AddRectFilled(new Vector2(center + t * barW * 0.5f, barPos.Y), new Vector2(center, barPos.Y + barH), fill, 3f);
            dl.AddLine(new Vector2(center, barPos.Y), new Vector2(center, barPos.Y + barH), ImGui.GetColorU32(HubStyle.Faint), 1f);
            if (best)
                dl.AddRect(barPos, barPos + new Vector2(barW, barH), ImGui.GetColorU32(HubStyle.Good), 3f, ImDrawFlags.None, 1.5f);

            dl.AddText(new Vector2(p0.X + labelW + barW + gap, p0.Y), txt, FormatEV(o.EV));
            ImGui.Dummy(new Vector2(full, barH + 4));
        }

        private static bool OptAvailable(EvalResult r, Move m) => r.Options.Any(o => o.Move == m && o.Available);

        private void SendButton(Move m, EvalResult r)
        {
            bool best = r.HasBest && m == r.Best;
            string phrase = PhraseFor(m);
            // The recommended move is the one button here that puts a message in
            // public chat, so it is the window's primary action.
            using IDisposable? primary = best ? HubStyle.Primary() : null;
            if (ImGui.Button($"{phrase}##say{m}"))
                Send(phrase);
        }

        private void Send(string phrase)
        {
            var msg = $"{c.ChatChannel} {phrase}".Trim();
            try { ECommons.Automation.Chat.SendMessage(msg); }
            catch (Exception ex) { Plugin.ChatGui.PrintError($"[Blackjack Advisor] Couldn't send \"{msg}\": {ex.Message}"); }
        }

        // ---- Entry controls ----------------------------------------------------------

        private void DrawControls()
        {
            using var _r = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f);

            ImGui.TextDisabled("Dealer up card");
            var dealer = parser.State.Dealer;
            for (int v = 1; v <= 10; v++)
            {
                if (v > 1) ImGui.SameLine();
                bool sel = dealer.HasValue && dealer.Value.Rank == RankLabel(v);
                // Selection reads as gold on the dark ramp, never a gold fill.
                if (sel)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, HubColors.Get("HubActive"));
                    ImGui.PushStyleColor(ImGuiCol.Text, HubStyle.Accent);
                }
                if (ImGui.Button($"{RankLabel(v)}##d{v}", new Vector2(34, 0)))
                {
                    parser.State.Dealer = new Card(RankLabel(v), '♠');
                    parser.State.FilledFromChat = false;
                    parser.NoteManualDealerCard(v);
                }
                if (sel) ImGui.PopStyleColor(2);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear##dclr")) parser.State.Dealer = null;

            ImGui.TextDisabled("Add to your hand   (10 = 10/J/Q/K)");
            for (int v = 1; v <= 10; v++)
            {
                if (v > 1) ImGui.SameLine();
                if (ImGui.Button($"{RankLabel(v)}##h{v}", new Vector2(34, 0)))
                {
                    parser.State.AddCard(RankLabel(v), fromChat: false);
                    parser.NoteManualCard(v);
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Undo")) { parser.State.RemoveLastCard(); parser.ClearManualEntries(); }
            ImGui.SameLine();
            if (ImGui.Button("Clear##hclr")) { parser.State.ClearHand(); parser.ClearManualEntries(); }

            if (ImGui.CollapsingHeader("Or enter a total directly"))
            {
                var t = parser.State.InTotal;
                if (ImGui.InputInt("Total", ref t)) { parser.State.InTotal = Math.Clamp(t, 2, 21); parser.State.TotalMode = true; parser.State.FilledFromChat = false; }
                bool sf = parser.State.InSoft;
                if (ImGui.Checkbox("Soft (has an ace counted as 11)", ref sf)) { parser.State.InSoft = sf; parser.State.TotalMode = true; parser.State.FilledFromChat = false; }
                bool pr = parser.State.InPair;
                if (ImGui.Checkbox("Pair (two equal cards)", ref pr)) { parser.State.InPair = pr; parser.State.TotalMode = true; parser.State.FilledFromChat = false; }
                if (parser.State.TotalMode && ImGui.Button("Back to card entry")) parser.State.TotalMode = false;
            }
        }

        // ---- Rules -------------------------------------------------------------------

        private void DrawRules()
        {
            var header = $"Rules: dealer stands on {c.DealerStandsOn}"
                       + (c.DealerHitsSoft17 ? $", hits soft {c.DealerStandsOn}" : "")
                       + (c.HostAllowsDouble ? ", double" : "")
                       + (c.HostAllowsSplit ? ", split" : "");
            if (!ImGui.CollapsingHeader(header)) return;

            int so = c.DealerStandsOn;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Dealer stands on", ref so))
            {
                c.DealerStandsOn = Math.Clamp(so, 12, 21);
                c.Save();
            }
            ImGui.SameLine();
            Help("The total the dealer stops drawing at. 17 is the casino rule; some hosts stop at 16, "
               + "which makes the dealer bust far less often and changes when you should stand.");

            if (parser.AnnouncedHouseRule is { } house && (house.Total != c.DealerStandsOn || house.HitsSoft != c.DealerHitsSoft17))
            {
                ImGui.TextColored(HubStyle.Warn,
                    $"This dealer said they stand on {house.Total}{(house.HitsSoft ? " and hit a soft one" : "")}.");
                ImGui.SameLine();
                if (ImGui.Button($"Use {house.Total}##house"))
                {
                    c.DealerStandsOn = house.Total;
                    c.DealerHitsSoft17 = house.HitsSoft;
                    c.Save();
                }
            }

            bool h17 = c.DealerHitsSoft17;
            if (ImGui.Checkbox($"Dealer hits soft {c.DealerStandsOn}", ref h17)) { c.DealerHitsSoft17 = h17; c.Save(); }
            ImGui.SameLine();
            Help($"On = the dealer draws again on a soft {c.DealerStandsOn} (an ace counted as 11). "
               + "Off = they stop. If unsure, ask the host.");

            bool das = c.DoubleAfterSplit;
            if (ImGui.Checkbox("Double after split allowed", ref das)) { c.DoubleAfterSplit = das; c.Save(); }
            bool ad = c.HostAllowsDouble;
            if (ImGui.Checkbox("Host offers Double Down", ref ad)) { c.HostAllowsDouble = ad; c.Save(); }
            bool asp = c.HostAllowsSplit;
            if (ImGui.Checkbox("Host offers Split", ref asp)) { c.HostAllowsSplit = asp; c.Save(); }

            ImGui.Spacing();
            ImGui.TextDisabled("Chat input");
            bool af = c.AutoFillFromChat;
            if (ImGui.Checkbox("Auto-fill from dealer's chat", ref af)) { c.AutoFillFromChat = af; c.Save(); }
            ImGui.SameLine();
            Help("Reads the dealer's messages and fills your hand + up card on your turn. Falls back to totals for dealers that don't print card symbols.");

            bool learn = c.LearnDealerWording;
            if (ImGui.Checkbox("Learn this dealer's wording", ref learn)) { c.LearnDealerWording = learn; c.Save(); }
            ImGui.SameLine();
            Help("Watches the totals the dealer announces and works out which lines mean what. Nothing is sent to chat and nothing leaves your machine.");

            var dn = c.DealerName;
            if (ImGui.InputText("Dealer name (optional)", ref dn, 48)) { c.DealerName = dn; c.Save(); }
            ImGui.SameLine(); Help("Only accept auto-fill from this sender. Leave blank to auto-detect the dealer.");

            bool dbg = c.ChatDebug;
            if (ImGui.Checkbox("Debug parser (log to chat)", ref dbg)) { c.ChatDebug = dbg; c.Save(); }
            ImGui.SameLine(); Help("Prints every chat line the parser sees and what it made of it. Use '/bj status' for the current state and '/bj parse' to force-read the last line.");

            bool dev = c.DevLog;
            if (ImGui.Checkbox("Send the parser trace to a dev log", ref dev)) { c.DevLog = dev; c.Save(); }
            ImGui.SameLine();
            ImGui.TextColored(Plugin.Telemetry.Active ? HubStyle.Good : HubStyle.Faint,
                Plugin.Telemetry.Active ? "sending" : "off");
            ImGui.SameLine(); Help("Posts the same trace to a log server on your network, so a whole table can be read back afterwards instead of scrolled through in chat.");

            var du = c.DevLogUrl;
            if (ImGui.InputText("Dev log URL", ref du, 128)) { c.DevLogUrl = du; c.Save(); }
            ImGui.SameLine(); Help("e.g. http://192.168.1.10:9999/log — leave blank to keep it off.");

            ImGui.Spacing();
            DrawLearnedLines();

            ImGui.Spacing();
            ImGui.TextDisabled("Chat output");
            var ch = c.ChatChannel;
            if (ImGui.InputText("Channel", ref ch, 16)) { c.ChatChannel = ch; c.Save(); }
            ImGui.SameLine(); Help("Prefix for sent messages, e.g. /p /say /sh /fc /echo");
            PhraseInput("Say for Stand", () => c.SayStand, v => c.SayStand = v);
            PhraseInput("Say for Hit", () => c.SayHit, v => c.SayHit = v);
            PhraseInput("Say for Double", () => c.SayDouble, v => c.SayDouble = v);
            PhraseInput("Say for Split", () => c.SaySplit, v => c.SaySplit = v);

            ImGui.Spacing();
            ImGui.TextDisabled("Exact EV for the in-game /random (infinite-deck) game.");
        }

        // ---- Learned dealer lines ------------------------------------------------------

        private void DrawLearnedLines()
        {
            if (!ImGui.CollapsingHeader("Learned dealer lines")) return;

            var all = parser.Store.All();
            var dealers = all.Select(l => l.Dealer).Where(d => d.Length > 0).Distinct().OrderBy(d => d).ToList();
            var options = new List<string> { "All dealers" };
            options.AddRange(dealers);
            var optArray = options.ToArray();

            // A filter left pointing at a dealer whose last line was just removed would show
            // "All dealers" while still hiding every row.
            if (learnedDealerFilter != null && !dealers.Contains(learnedDealerFilter)) learnedDealerFilter = null;
            int idx = learnedDealerFilter == null ? 0 : Math.Max(0, dealers.IndexOf(learnedDealerFilter) + 1);
            ImGui.SetNextItemWidth(240);
            if (ImGui.Combo("Dealer##learnedDealer", ref idx, optArray, optArray.Length))
                learnedDealerFilter = idx == 0 ? null : optArray[idx];

            var shown = learnedDealerFilter == null ? all : all.Where(l => l.Dealer == learnedDealerFilter).ToList();

            if (shown.Count == 0)
            {
                ImGui.TextColored(HubStyle.Muted,
                    "Nothing learned yet. This fills in by itself once the dealer announces a few totals.");
            }
            else
            {
                using var table = ImRaii.Table("##learnedLines", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
                if (table)
                {
                    ImGui.TableSetupColumn("Template", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 190f);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70f);
                    ImGui.TableHeadersRow();

                    foreach (var line in shown)
                    {
                        using var id = ImRaii.PushId(line.Template + "|" + line.Dealer);
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(line.Template);

                        ImGui.TableNextColumn();
                        int roleIdx = Array.IndexOf(RoleOrder, RoleIds.Parse(line.Role) ?? TemplateRole.Ignore);
                        if (roleIdx < 0) roleIdx = Array.IndexOf(RoleOrder, TemplateRole.Ignore);
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.Combo("##role", ref roleIdx, RoleLabels, RoleLabels.Length))
                            parser.SetLearnedRole(line.Template, line.Dealer, RoleIds.Id(RoleOrder[roleIdx]));

                        ImGui.TableNextColumn();
                        if (ImGui.Button("Remove"))
                            parser.RemoveLearnedLine(line.Template, line.Dealer);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Set a line to \"Ignore this line\" to stop it being learned again for good.");
                    }
                }
            }

            ImGui.TextColored(HubStyle.Faint, "<name> stands for any player's name, <n> for any number.");

            if (learnedDealerFilter != null)
            {
                int n = all.Count(l => l.Dealer == learnedDealerFilter);
                if (n > 0 && ImGui.Button($"Forget every line for {learnedDealerFilter}"))
                {
                    learnedForgetDealer = learnedDealerFilter;
                    learnedForgetOpen = true;
                }
            }

            DrawForgetLearnedModal();
        }

        private void DrawForgetLearnedModal()
        {
            if (learnedForgetOpen)
            {
                ImGui.OpenPopup("Forget learned lines");
                learnedForgetOpen = false;
            }

            using var modal = ImRaii.PopupModal("Forget learned lines", ImGuiWindowFlags.AlwaysAutoResize);
            if (!modal) return;

            int n = parser.Store.All().Count(l => l.Dealer == learnedForgetDealer);
            ImGui.TextWrapped($"Remove all {n} learned lines for {learnedForgetDealer}? They can be learned again.");
            ImGui.Spacing();
            if (ImGui.Button("Remove"))
            {
                parser.RemoveAllLearnedLinesFor(learnedForgetDealer);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        }

        // The theme editor is generated from the kit's option table, so this stays
        // one call however many themed values the kit grows.
        private static void DrawAppearance()
        {
            if (!ImGui.CollapsingHeader("Appearance")) return;
            ImGui.TextColored(HubStyle.Faint, "Shared with every XIV Hub plugin.");
            ImGui.Spacing();
            HubThemeEditor.Draw(Plugin.ThemeConfig);
        }

        private void PhraseInput(string label, Func<string> get, Action<string> set)
        {
            var v = get();
            if (ImGui.InputText(label, ref v, 64)) { set(v); c.Save(); }
        }

        // ---- Gil ledger --------------------------------------------------------------

        private unsafe int GetGil()
        {
            var im = InventoryManager.Instance();
            return im == null ? -1 : im->GetInventoryItemCount(1);
        }

        private void DrawLedger()
        {
            int gil = GetGil();
            if (gil < 0)
            {
                ImGui.TextDisabled("Gil: unavailable (log in to track).");
                return;
            }
            if (sessionStartGil < 0) sessionStartGil = gil;

            int net = gil - sessionStartGil;
            int hands = wins + losses + pushes;

            ImGui.Text($"Gil: {gil:N0}");
            ImGui.SameLine();
            ImGui.Text("   Session:");
            ImGui.SameLine();
            ImGui.TextColored(net > 0 ? HubStyle.Good : net < 0 ? HubStyle.Bad : HubStyle.Muted, $"{(net >= 0 ? "+" : "")}{net:N0}");

            ImGui.Text($"Hands: {hands}   W {wins}  L {losses}  P {pushes}");

            using var _r = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f);
            if (ImGui.Button("Win")) { wins++; NextRound(); }
            ImGui.SameLine();
            if (ImGui.Button("Lose")) { losses++; NextRound(); }
            ImGui.SameLine();
            if (ImGui.Button("Push")) { pushes++; NextRound(); }
            ImGui.SameLine();
            if (ImGui.Button("Reset session")) { sessionStartGil = gil; wins = losses = pushes = 0; }
            ImGui.TextDisabled("Net gil is read live from your inventory; W/L/P are manual.");
        }

        private void NextRound()
        {
            parser.NextRound();
        }

        // ---- Chat auto-fill ----------------------------------------------------------

        // Every channel a dealer plausibly speaks a macro on, or a player replies on. Everything
        // else (buffs, item use, system messages) can show an empty sender next to a bystander's
        // name and must never reach the learner or the built-in regexes as if it were dealer wording.
        private static readonly HashSet<XivChatType> SpeechTypes = new()
        {
            XivChatType.Say, XivChatType.Shout, XivChatType.TellIncoming, XivChatType.Party,
            XivChatType.Alliance, XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
            XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
            XivChatType.FreeCompany, XivChatType.NoviceNetwork, XivChatType.CustomEmote,
            XivChatType.StandardEmote, XivChatType.Yell, XivChatType.CrossParty,
            XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3,
            XivChatType.CrossLinkShell4, XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
            XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8, XivChatType.Echo,
        };

        private void OnChatMessage(IHandleableChatMessage handler)
        {
            if (!c.AutoFillFromChat) return;

            string text, sender;
            try { text = ChatText.Deglyph(handler.Message.TextValue); sender = ChatText.Deglyph(handler.Sender.TextValue ?? ""); }
            catch { return; }
            if (string.IsNullOrEmpty(text)) return;

            // The game's /random result carries a language-independent chat type; read the value from it
            // (last number, fullwidth-normalized for the JP client) rather than trusting localized text.
            int? roll = handler.LogKind == XivChatType.RandomNumber ? ChatParser.LastNumber(text) : null;

            var line = new ChatLine(handler.LogKind.ToString(), sender, text,
                IsSpeech: SpeechTypes.Contains(handler.LogKind), ChatParser.IsRollText(text), DateTime.Now);
            parser.Feed(line, roll);
        }

        public void Status()
        {
            var me = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "(none)";
            Plugin.ChatGui.Print($"[BJ] you='{me}'  auto-fill={(c.AutoFillFromChat ? "on" : "off")}");
            foreach (var line in parser.StatusLines())
                Plugin.ChatGui.Print($"[BJ] {line}");
        }

        public void ForceParseLast()
        {
            if (!parser.ForceParseLast())
                Plugin.ChatGui.Print("[BJ] No recent chat line to parse.");
        }

        // ---- Helpers -----------------------------------------------------------------

        private static string RankLabel(int v) => v == 1 ? "A" : v.ToString();

        private static string MoveLabel(Move m) => m switch
        {
            Move.Stand => "Stand",
            Move.Hit => "Hit",
            Move.Double => "Double Down",
            Move.Split => "Split",
            _ => m.ToString(),
        };

        private static Vector4 MoveColor(Move m) => m switch
        {
            Move.Stand => new(0.30f, 0.48f, 0.78f, 1f),
            Move.Hit => new(0.85f, 0.58f, 0.20f, 1f),
            Move.Double => new(0.28f, 0.68f, 0.38f, 1f),
            Move.Split => new(0.58f, 0.38f, 0.74f, 1f),
            _ => HubStyle.Muted,
        };

        private string PhraseFor(Move m) => m switch
        {
            Move.Stand => c.SayStand,
            Move.Hit => c.SayHit,
            Move.Double => c.SayDouble,
            Move.Split => c.SaySplit,
            _ => m.ToString(),
        };

        private static string FormatEV(double ev) => $"{ev * 100:+0.0;-0.0;0.0}%";

        private static void Help(string text)
        {
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
        }

        // One-sentence rationale for the recommended move.
        private string Explain(EvalResult r, HandState.Snapshot snap)
        {
            int bust = (int)Math.Round(Engine().DealerDistributionFromUp(snap.Dealer!.Value.Value)[0] * 100);
            string up = snap.Dealer.Value.Rank;

            if (r.Blackjack)
                return "Blackjack — you already won unless the dealer also draws to 21.";

            switch (r.Best)
            {
                case Move.Split when snap.Hand.Count == 2 && snap.Hand[0].Value == 1:
                    return "Split — two hands each starting on an ace beat one stiff hand by a mile.";
                case Move.Split when snap.Hand.Count == 2 && snap.Hand[0].Value == 8:
                    return $"Split — a pair of 8s is a losing 16; two fresh hands do better, and the dealer's {up} busts ~{bust}%.";
                case Move.Split:
                    return $"Split — turn one weak hand into two with a better start while the dealer's {up} busts ~{bust}%.";
                case Move.Double:
                    return r.Soft
                        ? $"Double — a free shot at a big total; the ace can't bust you and the dealer's {up} busts ~{bust}%."
                        : $"Double — {r.Total} lands 18-21 often and the dealer's {up} busts ~{bust}%, so press the bet.";
                case Move.Stand:
                    return r.Total >= 17
                        ? $"Stand — {r.Total} is strong; taking a card mostly just busts you."
                        : $"Stand — you'd likely bust, so let the dealer's weak {up} (busts ~{bust}%) do it instead.";
                case Move.Hit:
                    if (r.Total <= 11)
                        return r.Soft
                            ? $"Hit — a soft {r.Total} can't bust on one card, so improve it for free."
                            : "Hit — you can't bust and need a higher total.";
                    return r.Soft
                        ? $"Hit — a soft {r.Total} risks nothing on one card and the dealer's {up} is strong."
                        : $"Hit — {r.Total} loses if you stand and the dealer's {up} rarely busts (~{bust}%), so you must improve.";
            }
            return "";
        }
    }
}
