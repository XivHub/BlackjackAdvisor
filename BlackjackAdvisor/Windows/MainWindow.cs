using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using BlackjackAdvisor.Strategy;

namespace BlackjackAdvisor.Windows
{
    public class MainWindow : Window, IDisposable
    {
        private readonly Configuration c;

        private readonly record struct Card(string Rank, char Suit)
        {
            public int Value => Rank == "A" ? 1 : (Rank is "10" or "J" or "Q" or "K") ? 10 : int.Parse(Rank);
        }

        private readonly List<Card> hand = new();
        private Card? dealer;

        // Total-only input (dealers that announce a number, not cards).
        private bool totalMode;
        private int inTotal = 16;
        private bool inSoft;
        private bool inPair;

        // Chat auto-fill state
        private bool filledFromChat;
        private bool myTurn;
        private string? dealerSender;   // auto-locked dealer sender name
        private string? dealingTo;      // whose cards are being dealt right now ("Dealer" or a player name)
        private string? lastChatText;   // for /bj parse

        private BlackjackEngine? engine;
        private bool lastH17, lastDas;

        // Gil ledger
        private int sessionStartGil = -1;
        private int wins, losses, pushes;

        // Crisp large font (baked once, downscaled when drawn -> sharp).
        private readonly IFontHandle bigFont;

        private static readonly char[] Suits = { '♠', '♥', '♣', '♦' };

        // Palette
        private static readonly Vector4 Felt = new(0.09f, 0.28f, 0.17f, 1f);
        private static readonly Vector4 FeltRim = new(0.04f, 0.15f, 0.09f, 1f);
        private static readonly Vector4 FeltText = new(0.72f, 0.86f, 0.74f, 1f);
        private static readonly Vector4 CardFace = new(0.97f, 0.97f, 0.95f, 1f);
        private static readonly Vector4 CardEdge = new(0.14f, 0.14f, 0.16f, 1f);
        private static readonly Vector4 SuitRed = new(0.78f, 0.13f, 0.13f, 1f);
        private static readonly Vector4 SuitBlack = new(0.10f, 0.10f, 0.12f, 1f);
        private static readonly Vector4 Pill = new(0.18f, 0.18f, 0.22f, 1f);
        private static readonly Vector4 Green = new(0.35f, 0.82f, 0.42f, 1f);
        private static readonly Vector4 Red = new(0.90f, 0.35f, 0.35f, 1f);
        private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);
        private static readonly Vector4 Gold = new(0.95f, 0.80f, 0.30f, 1f);
        private static readonly Vector4 White = new(1f, 1f, 1f, 1f);

        public MainWindow(Configuration cfg) : base("Blackjack Advisor")
        {
            c = cfg;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(400, 440),
                MaximumSize = new Vector2(900, 1400),
            };
            bigFont = Plugin.PluginInterface.UiBuilder.FontAtlas
                .NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(34f)));
            Plugin.ChatGui.ChatMessage += OnChatMessage;
        }

        public void Dispose()
        {
            Plugin.ChatGui.ChatMessage -= OnChatMessage;
            bigFont.Dispose();
        }

        private BlackjackEngine Engine()
        {
            if (engine == null || lastH17 != c.DealerHitsSoft17 || lastDas != c.DoubleAfterSplit)
            {
                engine = new BlackjackEngine(c.DealerHitsSoft17, c.DoubleAfterSplit);
                lastH17 = c.DealerHitsSoft17;
                lastDas = c.DoubleAfterSplit;
            }
            return engine;
        }

        public override void Draw()
        {
            bool ready = dealer.HasValue && (totalMode || hand.Count > 0);
            EvalResult? r = ready ? Evaluate() : null;

            DrawRules();
            ImGui.Spacing();
            DrawTable(r);
            ImGui.Spacing();
            DrawRecommendation(r);
            ImGui.Separator();
            DrawControls();
            ImGui.Separator();
            DrawLedger();
        }

        private EvalResult Evaluate() => totalMode
            ? Engine().EvaluateTotal(inTotal, inSoft, inPair, dealer!.Value.Value, c.HostAllowsDouble, c.HostAllowsSplit)
            : Engine().Evaluate(hand.Select(h => h.Value).ToList(), dealer!.Value.Value, c.HostAllowsDouble, c.HostAllowsSplit);

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

        // ---- The felt table ----------------------------------------------------------

        private void DrawTable(EvalResult? r)
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
            if (dealer.HasValue) CardWidget(dealer.Value.Rank, dealer.Value.Suit, false, cardSize);
            else CardWidget("", '?', true, cardSize);

            ImGui.Dummy(new Vector2(0, 6));

            ImGui.TextColored(FeltText, "YOU");
            if (filledFromChat)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.78f, 0.95f, 1f), "· auto-filled from chat");
            }

            if (totalMode)
            {
                DrawPill($"Total {inTotal}{(inSoft ? " soft" : "")}{(inPair ? " pair" : "")}", Pill, CardFace, 20f);
            }
            else if (hand.Count == 0)
            {
                CardWidget("", '?', true, cardSize);
            }
            else
            {
                for (int i = 0; i < hand.Count; i++)
                {
                    if (i > 0) ImGui.SameLine(0, 6);
                    CardWidget(hand[i].Rank, hand[i].Suit, false, cardSize);
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

        private void DrawRecommendation(EvalResult? r)
        {
            if (r == null)
            {
                ImGui.TextColored(Grey, "Pick the dealer's up card and your cards below.");
                return;
            }

            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            const float bh = 38;

            string label;
            Vector4 col;
            if (r.Blackjack) { label = "BLACKJACK!"; col = Gold; }
            else if (r.Bust) { label = "BUST"; col = Red; }
            else if (r.HasBest) { label = MoveLabel(r.Best).ToUpperInvariant(); col = MoveColor(r.Best); }
            else { label = "-"; col = Grey; }

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
                ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.86f, 1f), Explain(r));
                ImGui.PopTextWrapPos();
            }

            ImGui.Dummy(new Vector2(0, 2));
            foreach (var o in r.Options)
                if (o.Available)
                    EVBar(r, o);

            // Manual chat send (never automatic)
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.TextColored(Grey, $"Say in {c.ChatChannel} (click to send):");
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

            uint txt = ImGui.GetColorU32(best ? Green : new Vector4(0.85f, 0.85f, 0.85f, 1f));
            dl.AddText(p0, txt, MoveLabel(o.Move));

            var barPos = new Vector2(p0.X + labelW, p0.Y);
            dl.AddRectFilled(barPos, barPos + new Vector2(barW, barH), ImGui.GetColorU32(new Vector4(0.17f, 0.17f, 0.19f, 1f)), 3f);
            float center = barPos.X + barW * 0.5f;
            float t = Math.Clamp((float)o.EV, -1.5f, 1.5f) / 1.5f;
            uint fill = ImGui.GetColorU32(o.EV >= 0 ? new Vector4(0.30f, 0.70f, 0.35f, 1f) : new Vector4(0.80f, 0.32f, 0.32f, 1f));
            if (t >= 0)
                dl.AddRectFilled(new Vector2(center, barPos.Y), new Vector2(center + t * barW * 0.5f, barPos.Y + barH), fill, 3f);
            else
                dl.AddRectFilled(new Vector2(center + t * barW * 0.5f, barPos.Y), new Vector2(center, barPos.Y + barH), fill, 3f);
            dl.AddLine(new Vector2(center, barPos.Y), new Vector2(center, barPos.Y + barH), ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f)), 1f);
            if (best)
                dl.AddRect(barPos, barPos + new Vector2(barW, barH), ImGui.GetColorU32(Green), 3f, ImDrawFlags.None, 1.5f);

            dl.AddText(new Vector2(p0.X + labelW + barW + gap, p0.Y), txt, FormatEV(o.EV));
            ImGui.Dummy(new Vector2(full, barH + 4));
        }

        private static bool OptAvailable(EvalResult r, Move m) => r.Options.Any(o => o.Move == m && o.Available);

        private void SendButton(Move m, EvalResult r)
        {
            bool best = r.HasBest && m == r.Best;
            string phrase = PhraseFor(m);
            if (best) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.22f, 1f));
            if (ImGui.Button($"{phrase}##say{m}"))
                Send(phrase);
            if (best) ImGui.PopStyleColor();
        }

        private void Send(string phrase)
        {
            var msg = $"{c.ChatChannel} {phrase}".Trim();
            try { Chat.SendMessage(msg); }
            catch (Exception ex) { Plugin.ChatGui.PrintError($"[Blackjack Advisor] Couldn't send \"{msg}\": {ex.Message}"); }
        }

        // ---- Entry controls ----------------------------------------------------------

        private void DrawControls()
        {
            using var _r = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f);

            ImGui.TextDisabled("Dealer up card");
            for (int v = 1; v <= 10; v++)
            {
                if (v > 1) ImGui.SameLine();
                bool sel = dealer.HasValue && dealer.Value.Rank == RankLabel(v);
                if (sel) ImGui.PushStyleColor(ImGuiCol.Button, Gold);
                if (ImGui.Button($"{RankLabel(v)}##d{v}", new Vector2(34, 0)))
                {
                    dealer = new Card(RankLabel(v), '♠');
                    filledFromChat = false;
                }
                if (sel) ImGui.PopStyleColor();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear##dclr")) dealer = null;

            ImGui.TextDisabled("Add to your hand   (10 = 10/J/Q/K)");
            for (int v = 1; v <= 10; v++)
            {
                if (v > 1) ImGui.SameLine();
                if (ImGui.Button($"{RankLabel(v)}##h{v}", new Vector2(34, 0)))
                {
                    hand.Add(new Card(RankLabel(v), Suits[hand.Count % 4]));
                    totalMode = false;
                    filledFromChat = false;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Undo") && hand.Count > 0) { hand.RemoveAt(hand.Count - 1); filledFromChat = false; }
            ImGui.SameLine();
            if (ImGui.Button("Clear##hclr")) { hand.Clear(); totalMode = false; filledFromChat = false; }

            if (ImGui.CollapsingHeader("Or enter a total directly"))
            {
                var t = inTotal;
                if (ImGui.InputInt("Total", ref t)) { inTotal = Math.Clamp(t, 2, 21); totalMode = true; filledFromChat = false; }
                bool sf = inSoft;
                if (ImGui.Checkbox("Soft (has an ace counted as 11)", ref sf)) { inSoft = sf; totalMode = true; filledFromChat = false; }
                bool pr = inPair;
                if (ImGui.Checkbox("Pair (two equal cards)", ref pr)) { inPair = pr; totalMode = true; filledFromChat = false; }
                if (totalMode && ImGui.Button("Back to card entry")) totalMode = false;
            }
        }

        // ---- Rules -------------------------------------------------------------------

        private void DrawRules()
        {
            var header = $"Rules: dealer {(c.DealerHitsSoft17 ? "hits" : "stands on")} soft 17"
                       + (c.HostAllowsDouble ? ", double" : "")
                       + (c.HostAllowsSplit ? ", split" : "");
            if (!ImGui.CollapsingHeader(header)) return;

            bool h17 = c.DealerHitsSoft17;
            if (ImGui.Checkbox("Dealer hits soft 17 (H17)", ref h17)) { c.DealerHitsSoft17 = h17; c.Save(); }
            ImGui.SameLine();
            Help("Off = dealer stands on soft 17 (S17). If unsure, ask the host.");

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

            var dn = c.DealerName;
            if (ImGui.InputText("Dealer name (optional)", ref dn, 48)) { c.DealerName = dn; c.Save(); }
            ImGui.SameLine(); Help("Only accept auto-fill from this sender. Leave blank to auto-detect the dealer.");

            bool dbg = c.ChatDebug;
            if (ImGui.Checkbox("Debug parser (log to chat)", ref dbg)) { c.ChatDebug = dbg; c.Save(); }
            ImGui.SameLine(); Help("Prints what the parser extracts from each candidate line. Use '/bj parse' to force-read the last line.");

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
            ImGui.TextColored(net > 0 ? Green : net < 0 ? Red : Grey, $"{(net >= 0 ? "+" : "")}{net:N0}");

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
            hand.Clear();
            dealer = null;
            totalMode = false;
            filledFromChat = false;
            dealingTo = null;
        }

        // ---- Chat auto-fill ----------------------------------------------------------

        // Card token in either order, case-insensitive, T or 10 for ten, optional space.
        private static readonly Regex CardRx = new(
            @"(?:([♣♠♦♥])\s*(10|[2-9]|[TtAaJjQqKk])|(10|[2-9]|[TtAaJjQqKk])\s*([♣♠♦♥]))", RegexOptions.Compiled);
        private static readonly Regex AceGlyphRx = new(@"[♣♠♦♥]\s*[Aa]|[Aa]\s*[♣♠♦♥]", RegexOptions.Compiled);
        // Prefer an explicit "Total: N"; the gap is only spaces/colons so it never skips over a card glyph to a rank digit.
        private static readonly Regex TotalKwRx = new(@"total[\s:]*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HandNumRx = new(@"(?:hand(?:\s+is)?|have)[\s:]*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DealerTokenRx = new(@"([♣♠♦♥])\s*(10|[2-9]|[TtAaJjQqKk])|(10|[2-9]|[TtAaJjQqKk])\s*([♣♠♦♥])|\b(10|[2-9]|[TtAaJjQqKk])\b", RegexOptions.Compiled);
        private static readonly Regex NamePrefixRx = new(@"^\s*([^,]+?),\s*your\s+hand", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TurnRx = new(@"'s Turn", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DealingRx = new(@"Dealing\s+(.+?)'s\s+Cards", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A /random result: "(1-13) 9", tolerant of locale prefix (Random!/Würfeln!), en/em dashes and spacing.
        private static readonly Regex RandomRx = new(@"\(\s*\d{1,2}\s*[-–—]\s*\d{1,2}\s*\)\s*(\d{1,2})", RegexOptions.Compiled);
        // "<name> rolls a 5" style (some tables/RP dealers).
        private static readonly Regex RollsRx = new(@"\brolls?\s+(?:a\s+)?(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DigitRunRx = new(@"\d+", RegexOptions.Compiled);
        private static readonly Regex SummaryRx = new(@"([^,]+?)'s\s+hand\s+is\s+(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void Dbg(string msg)
        {
            if (c.ChatDebug) Plugin.ChatGui.Print($"[BJ] {msg}");
        }

        private void OnChatMessage(IHandleableChatMessage handler)
        {
            if (!c.AutoFillFromChat) return;

            string text, sender;
            try { text = handler.Message.TextValue; sender = handler.Sender.TextValue ?? ""; }
            catch { return; }
            if (string.IsNullOrEmpty(text)) return;
            lastChatText = text;

            // The game's /random result carries a language-independent chat type; read the value from it
            // (last number, fullwidth-normalized for the JP client) rather than trusting localized text.
            int? roll = handler.LogKind == XivChatType.RandomNumber ? LastNumber(text) : null;

            // Auto-lock the dealer from strong, unmistakable macro markers only.
            bool marker = roll.HasValue
                          || RandomRx.IsMatch(text)
                          || TurnRx.IsMatch(text)
                          || text.Contains("Dealer's Hand", StringComparison.OrdinalIgnoreCase);
            if (marker && !string.IsNullOrEmpty(sender)) dealerSender = sender;

            HandleLine(text, sender, manual: false, roll);
        }

        public void ForceParseLast()
        {
            if (string.IsNullOrEmpty(lastChatText)) { Plugin.ChatGui.Print("[BJ] No recent chat line to parse."); return; }
            HandleLine(lastChatText, "", manual: true, null);
        }

        private void HandleLine(string text, string sender, bool manual, int? roll)
        {
            var me = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
            if (string.IsNullOrEmpty(me)) { Dbg("no local player name"); return; }

            bool mentionsHand = text.Contains("hand", StringComparison.OrdinalIgnoreCase);
            var oic = StringComparison.OrdinalIgnoreCase;

            // "Dealing <who>'s Cards" -> new round; start previewing if it's my deal.
            var deal = DealingRx.Match(text);
            if (deal.Success)
            {
                if (!manual && !SenderIsDealer(sender)) return;
                myTurn = false;
                string who = deal.Groups[1].Value.Trim();
                dealingTo = who.Contains("Dealer", oic) ? "Dealer" : who;
                if (dealingTo.Equals(me, oic)) { hand.Clear(); dealer = null; totalMode = false; filledFromChat = false; }
                Dbg($"dealing to {dealingTo}");
                return;
            }

            // Turn header: capture the dealer's up-card draw; stop previewing on player turns.
            if (!mentionsHand && text.Contains("Turn", oic))
            {
                bool dealerTurn = text.Contains("Dealer", oic);
                myTurn = !dealerTurn && text.Contains(me, oic);
                dealingTo = dealerTurn ? "Dealer" : null;
                Dbg($"turn -> myTurn={myTurn}, dealingTo={dealingTo ?? "-"}");
                return;
            }

            // Preview: a /random draw during dealing. Prefer the chat-type value, then text fallbacks.
            if (dealingTo != null)
            {
                int? rv = roll ?? RollValueFromText(text);
                if (rv is >= 1 and <= 13)
                {
                    if (!manual && !SenderIsDealer(sender)) return;
                    if (dealingTo.Equals(me, oic))
                    {
                        hand.Add(new Card(RankFromRandom(rv.Value), Suits[hand.Count % 4]));
                        totalMode = false; filledFromChat = true;
                        Dbg($"preview draw {RankFromRandom(rv.Value)}");
                    }
                    else if (dealingTo == "Dealer")
                    {
                        dealer = new Card(RankFromRandom(rv.Value), '♠');
                        Dbg($"dealer up {dealer.Value.Rank}");
                    }
                    return;
                }
            }

            // Preview fallback: "<me>'s hand is N" summary (used only if no cards were captured).
            if (mentionsHand && !text.Contains("your hand", oic))
            {
                var sum = SummaryRx.Match(text);
                if (sum.Success && sum.Groups[1].Value.Trim().Equals(me, oic))
                {
                    if (!manual && !SenderIsDealer(sender)) return;
                    if (hand.Count == 0 && int.TryParse(sum.Groups[2].Value, out int tn) && tn is >= 2 and <= 21)
                    {
                        inTotal = tn; inSoft = text.Contains("or 11", oic); inPair = false;
                        totalMode = true; filledFromChat = true;
                        Dbg($"preview total {tn}");
                    }
                    return;
                }
            }

            if (text.Contains("stays with", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("busted", StringComparison.OrdinalIgnoreCase)) return;

            // Hand-line trigger: literal "your hand", or the decision prompt.
            bool handLine = text.Contains("your hand", StringComparison.OrdinalIgnoreCase)
                            || (text.Contains("hit", StringComparison.OrdinalIgnoreCase)
                                && text.Contains("stand", StringComparison.OrdinalIgnoreCase));
            if (!handLine) return;

            // Sender must be the dealer (auto-locked or configured), unless forced.
            if (!manual && !SenderIsDealer(sender))
            {
                Dbg($"skip: sender '{sender}' != dealer '{Dealer()}'");
                return;
            }

            // Ownership: name prefix wins, else current turn.
            var nm = NamePrefixRx.Match(text);
            bool mine = manual
                || (nm.Success ? nm.Groups[1].Value.Trim().Equals(me, StringComparison.OrdinalIgnoreCase) : myTurn);
            if (!mine) { Dbg("skip: not my hand"); return; }

            ParseAndFill(text);
        }

        private string Dealer() => !string.IsNullOrWhiteSpace(c.DealerName) ? c.DealerName : dealerSender ?? "";

        private bool SenderIsDealer(string sender)
        {
            if (!string.IsNullOrWhiteSpace(c.DealerName))
                return sender.Contains(c.DealerName, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(dealerSender)) return true; // not locked yet -> allow bootstrap
            return string.Equals(sender, dealerSender, StringComparison.OrdinalIgnoreCase);
        }

        private void ParseAndFill(string text)
        {
            int di = text.IndexOf("dealer", StringComparison.OrdinalIgnoreCase);
            string playerPart = di >= 0 ? text[..di] : text;
            string dealerPart = di >= 0 ? text[di..] : "";

            var d = ParseDealer(dealerPart);
            var cards = ParseCards(playerPart);
            int? stated = ParseStatedTotal(playerPart);

            if (cards.Count > 0)
            {
                // With real cards, only an explicit "Total: N" may override them (self-heal on parse gaps).
                int computed = HandTotal(cards, out _);
                var tk = TotalKwRx.Match(playerPart);
                if (tk.Success && int.TryParse(tk.Groups[1].Value, out int st) && st is >= 2 and <= 21 && st != computed)
                {
                    Dbg($"card/total mismatch (cards={computed}, stated={st}) -> total mode");
                    SetTotal(st, playerPart, d);
                    return;
                }
                hand.Clear();
                hand.AddRange(cards);
                totalMode = false;
                if (d.HasValue) dealer = d.Value;
                filledFromChat = true;
                Dbg($"cards {string.Concat(cards.Select(x => x.Rank))} vs dealer {(d?.Rank ?? "?")}");
                return;
            }

            if (stated.HasValue) { SetTotal(stated.Value, playerPart, d); Dbg($"total {stated} vs dealer {(d?.Rank ?? "?")}"); }
            else Dbg("no hand data parsed");
        }

        private void SetTotal(int total, string playerPart, Card? d)
        {
            inTotal = Math.Clamp(total, 2, 21);
            inSoft = playerPart.Contains("or 11", StringComparison.OrdinalIgnoreCase) || AceGlyphRx.IsMatch(playerPart);
            inPair = false;
            totalMode = true;
            if (d.HasValue) dealer = d.Value;
            filledFromChat = true;
        }

        private static List<Card> ParseCards(string s)
        {
            var list = new List<Card>();
            foreach (Match m in CardRx.Matches(s))
            {
                if (m.Groups[1].Success) list.Add(new Card(NormalizeRank(m.Groups[2].Value), m.Groups[1].Value[0]));
                else list.Add(new Card(NormalizeRank(m.Groups[3].Value), m.Groups[4].Value[0]));
            }
            return list;
        }

        private static Card? ParseDealer(string dealerPart)
        {
            if (string.IsNullOrEmpty(dealerPart)) return null;
            if (dealerPart.Contains("or 11", StringComparison.OrdinalIgnoreCase)) return new Card("A", '♠');
            var m = DealerTokenRx.Match(dealerPart);
            if (!m.Success) return null;
            if (m.Groups[1].Success) return new Card(NormalizeRank(m.Groups[2].Value), m.Groups[1].Value[0]);
            if (m.Groups[3].Success) return new Card(NormalizeRank(m.Groups[3].Value), m.Groups[4].Value[0]);
            return new Card(NormalizeRank(m.Groups[5].Value), '♠');
        }

        private static int? ParseStatedTotal(string playerPart)
        {
            var m = TotalKwRx.Match(playerPart);
            if (!m.Success) m = HandNumRx.Match(playerPart);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int t) && t is >= 2 and <= 21) return t;
            return null;
        }

        // Result value from /random text: "(1-13) 9" or "<name> rolls a 9".
        private static int? RollValueFromText(string text)
        {
            var m = RandomRx.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;
            var r = RollsRx.Match(text);
            if (r.Success && int.TryParse(r.Groups[1].Value, out int v2)) return v2;
            return null;
        }

        // Last integer in a string, tolerant of the JP client's fullwidth digits.
        private static int? LastNumber(string text)
        {
            var s = NormalizeDigits(text);
            var ms = DigitRunRx.Matches(s);
            if (ms.Count == 0) return null;
            return int.TryParse(ms[^1].Value, out int v) ? v : null;
        }

        private static string NormalizeDigits(string s)
        {
            char[]? a = null;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= '０' && s[i] <= '９')
                {
                    a ??= s.ToCharArray();
                    a[i] = (char)('0' + (s[i] - '０'));
                }
            return a == null ? s : new string(a);
        }

        private static int HandTotal(List<Card> cards, out bool soft)
        {
            int total = 0; soft = false;
            foreach (var c in cards)
            {
                int raw = soft ? total - 10 : total;
                raw += c.Value == 1 ? 1 : c.Value;
                bool has = soft || c.Value == 1;
                if (has && raw + 10 <= 21) { total = raw + 10; soft = true; }
                else { total = raw; soft = false; }
            }
            return total;
        }

        private static string NormalizeRank(string r)
        {
            r = r.ToUpperInvariant();
            return r is "T" or "J" or "Q" or "K" ? "10" : r;
        }

        // ---- Helpers -----------------------------------------------------------------

        private static string RankLabel(int v) => v == 1 ? "A" : v.ToString();

        // A /random 1-13 draw -> card rank (1=A, 11/12/13 = J/Q/K, all worth 10).
        private static string RankFromRandom(int n) => n switch
        {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            10 => "10",
            _ => (n >= 2 && n <= 9) ? n.ToString() : "10",
        };

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
            _ => Grey,
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
        private string Explain(EvalResult r)
        {
            int bust = (int)Math.Round(Engine().DealerDistributionFromUp(dealer!.Value.Value)[0] * 100);
            string up = dealer.Value.Rank;

            if (r.Blackjack)
                return "Blackjack — you already won unless the dealer also draws to 21.";

            switch (r.Best)
            {
                case Move.Split when hand.Count == 2 && hand[0].Value == 1:
                    return "Split — two hands each starting on an ace beat one stiff hand by a mile.";
                case Move.Split when hand.Count == 2 && hand[0].Value == 8:
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
