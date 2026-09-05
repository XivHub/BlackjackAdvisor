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
using XivHubPluginKit.UI;

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
        private int? houseStandsOn;     // the draw threshold the dealer announced, if they did
        private bool houseHitsSoft;     // whether that announcement implies a soft threshold is hit
        private bool filledFromChat;
        private bool myTurn;
        private string? dealerSender;   // auto-locked dealer sender name
        private string? dealingTo;      // whose cards are being dealt right now ("Dealer" or a player name)
        private string? lastChatText;   // for /bj parse

        private BlackjackEngine? engine;
        private bool lastH17, lastDas;
        private int lastStandOn;

        // Gil ledger
        private int sessionStartGil = -1;
        private int wins, losses, pushes;

        // Crisp large font (baked once, downscaled when drawn -> sharp).
        private readonly IFontHandle bigFont;

        private static readonly char[] Suits = { '♠', '♥', '♣', '♦' };

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
            bool ready = dealer.HasValue && (totalMode || hand.Count > 0);
            EvalResult? r = ready ? Evaluate() : null;

            Plugin.Telemetry.Snapshot(() =>
                $"you={(totalMode ? $"total {inTotal}{(inSoft ? " soft" : "")}" : hand.Count == 0 ? "-" : string.Concat(hand.Select(h => h.Rank)))}"
                + $" up={dealer?.Rank ?? "-"} dealingTo={dealingTo ?? "-"} myTurn={myTurn}"
                + $" filled={filledFromChat} best={(r?.HasBest == true ? r.Best.ToString() : "-")}");

            DrawRules();
            ImGui.Spacing();
            DrawTable(r);
            ImGui.Spacing();
            DrawRecommendation(r);
            ImGui.Separator();
            DrawControls();
            ImGui.Separator();
            DrawLedger();
            ImGui.Separator();
            DrawAppearance();
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
                ImGui.TextColored(HubStyle.Info, "· auto-filled from chat");
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
                ImGui.TextColored(HubStyle.Muted, Explain(r));
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
                // Selection reads as gold on the dark ramp, never a gold fill.
                if (sel)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, HubColors.Get("HubActive"));
                    ImGui.PushStyleColor(ImGuiCol.Text, HubStyle.Accent);
                }
                if (ImGui.Button($"{RankLabel(v)}##d{v}", new Vector2(34, 0)))
                {
                    dealer = new Card(RankLabel(v), '♠');
                    filledFromChat = false;
                }
                if (sel) ImGui.PopStyleColor(2);
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

            if (AnnouncedHouseRule is { } house)
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
        // The dealer addresses one player: "<name>, your hand is ..." / "<name>, would you like to ...".
        private static readonly Regex NamePrefixRx = new(
            @"^\s*([^,]+?),\s*(?:your\s+hand|would\s+you\s+like|what\s+would\s+you\s+like|do\s+you\s+want)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TurnRx = new(@"'s Turn", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DealingRx = new(@"Dealing\s+(.+?)'s\s+Cards", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "Here is your first two Cards <name>!" — the name trails the line instead of taking a possessive.
        private static readonly Regex FirstCardsRx = new(@"\bfirst\s+two\s+cards?\b[:\s]*(.+?)[\s!?]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "Time to reveal the Dealer's first Card!" — some macros drop the name ("the 's first Card").
        private static readonly Regex RevealRx = new(@"\breveal\b[^.!?]*?\b(first|second|next)\s+card", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "<name> chooses to Double Down!" / "<name> chooses Hit!" / "<name> is forced to Stand!"
        private static readonly Regex ActionRx = new(
            @"^\s*(.+?)\s+(?:chooses|choose|decides|opts|wants|is\s+forced)(?:\s+to)?\s+(hit|stand|double|split)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A hand announced as a number on its own line: "15", "1 or 11", "1/11", "Blackjack 16",
        // or with the pair a dealer offers to split: "14 or 7/7 splits". An ace is written both
        // ways, so the last number is the soft reading either way.
        private static readonly Regex BareTotalRx = new(
            @"^[\s\-–—]*(?:blackjack|total|score|hand)?[\s:!.]*(\d{1,2})"
            + @"(?:\s*or\s*(\d{1,2})\s*/\s*(\d{1,2})\s*splits?|\s*(?:or|/)\s*(\d{1,2}))?\s*[.!]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A /random result: "(1-13) 9", tolerant of locale prefix (Random!/Würfeln!), en/em dashes and spacing.
        private static readonly Regex RandomRx = new(@"\(\s*\d{1,2}\s*[-–—]\s*\d{1,2}\s*\)\s*(\d{1,2})", RegexOptions.Compiled);
        // "<name> rolls a 5" style (some tables/RP dealers).
        private static readonly Regex RollsRx = new(@"\brolls?\s+(?:a\s+)?(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DigitRunRx = new(@"\d+", RegexOptions.Compiled);
        // Dealers state the house rule in passing: "DEALER is below 16 and rolls again.",
        // "Dealer stands on 17". Worth reading — the draw threshold moves the advice on every
        // stiff hand, and it is the one rule a player is least likely to think to ask about.
        private static readonly Regex HouseDrawsRx = new(
            @"\bbelow\s+(\d{1,2})\b.{0,20}?\b(?:rolls?|draws?|hits?)\s+again", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HouseStandsRx = new(
            @"\bstands?\s+(?:on|at)\s+(?:soft\s+|hard\s+|all\s+)?(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SummaryRx = new(@"([^,]+?)'s\s+hand\s+is\s+(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The parser trace goes to the dev log whenever one is configured, and to game chat only
        // when the user asked to see it there.
        private void Dbg(string msg)
        {
            Plugin.Telemetry.Log(msg);
            if (c.ChatDebug) Plugin.ChatGui.Print($"[BJ] {msg}");
        }

        private void OnChatMessage(IHandleableChatMessage handler)
        {
            if (!c.AutoFillFromChat) return;

            string text, sender;
            try { text = Deglyph(handler.Message.TextValue); sender = Deglyph(handler.Sender.TextValue ?? ""); }
            catch { return; }
            if (string.IsNullOrEmpty(text)) return;
            lastChatText = text;
            Dbg($"«{handler.LogKind}» [{sender}] {(text.Length > 100 ? text[..100] : text)}");

            // The game's /random result carries a language-independent chat type; read the value from it
            // (last number, fullwidth-normalized for the JP client) rather than trusting localized text.
            int? roll = handler.LogKind == XivChatType.RandomNumber ? LastNumber(text) : null;

            // Auto-lock the dealer from strong, unmistakable macro markers only.
            bool marker = roll.HasValue
                          || RandomRx.IsMatch(text)
                          || TurnRx.IsMatch(text)
                          || text.Contains("Dealer's Hand", StringComparison.OrdinalIgnoreCase);
            if (marker && !string.IsNullOrEmpty(sender)) dealerSender = CleanName(sender);

            HandleLine(text, sender, manual: false, roll);
        }

        public void Status()
        {
            var me = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "(none)";
            Plugin.ChatGui.Print($"[BJ] you='{me}'  dealer='{Dealer()}'  auto-fill={(c.AutoFillFromChat ? "on" : "off")}");
            Plugin.ChatGui.Print($"[BJ] dealing to='{dealingTo ?? "-"}'  my turn={myTurn}  "
                + $"hand={(totalMode ? $"total {inTotal}" : hand.Count == 0 ? "-" : string.Concat(hand.Select(h => h.Rank + " ")))} "
                + $"up card={(dealer?.Rank ?? "-")}");
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

            if (manual || SenderIsDealer(sender)) ReadHouseRule(text);

            // Round start: "Dealing <who>'s Cards" or "Here is your first two Cards <who>!".
            var deal = DealingRx.Match(text);
            if (!deal.Success) deal = FirstCardsRx.Match(text);
            if (deal.Success)
            {
                if (!manual && !SenderIsDealer(sender)) { Dbg($"deal line from '{sender}' != dealer '{Dealer()}'"); return; }
                myTurn = false;
                string who = deal.Groups[1].Value.Trim();
                dealingTo = who.Contains("Dealer", oic) ? "Dealer" : who;
                bool mineDeal = NameIs(who, me);
                if (mineDeal) { hand.Clear(); dealer = null; totalMode = false; filledFromChat = false; }
                Dbg($"dealing to {dealingTo}{(mineDeal ? " (you)" : $", you are {me}")}");
                return;
            }

            // The dealer's own card is drawn next; a first-card reveal starts a fresh up card.
            var reveal = RevealRx.Match(text);
            if (reveal.Success)
            {
                if (!manual && !SenderIsDealer(sender)) return;
                myTurn = false;
                dealingTo = "Dealer";
                if (reveal.Groups[1].Value.Equals("first", oic)) dealer = null;
                Dbg("dealer reveal");
                return;
            }

            // Turn header: capture the dealer's up-card draw; stop previewing on player turns.
            if (!mentionsHand && text.Contains("Turn", oic))
            {
                bool dealerTurn = text.Contains("Dealer", oic);
                myTurn = !dealerTurn && NameMentioned(text, me);
                dealingTo = dealerTurn ? "Dealer" : null;
                Dbg($"turn -> myTurn={myTurn}, dealingTo={dealingTo ?? "-"}");
                return;
            }

            // A hand is over, so nobody draws until the next player is prompted.
            if (text.Contains("stays with", oic) || text.Contains("busted", oic)
                || text.Contains("got a blackjack", oic) || text.Contains("has a blackjack", oic))
            {
                dealingTo = null;
                myTurn = false;
                return;
            }

            // "<who> chooses to Hit!" — that player receives the cards drawn next.
            var act = ActionRx.Match(text);
            if (act.Success && (manual || SenderIsDealer(sender)))
            {
                string who = act.Groups[1].Value.Trim();
                bool ended = act.Groups[2].Value.StartsWith("stand", oic);
                dealingTo = ended ? null : who;
                myTurn = !ended && NameIs(who, me);
                Dbg($"{who} -> {act.Groups[2].Value}, myTurn={myTurn}");
                return;
            }

            // "<who>, would you like to hit, stand or double down?" — same, plus it may carry the hand.
            var prompt = NamePrefixRx.Match(text);
            if (prompt.Success && (manual || SenderIsDealer(sender)))
            {
                string who = prompt.Groups[1].Value.Trim();
                dealingTo = who;
                myTurn = NameIs(who, me);
            }

            if (dealingTo != null)
            {
                // Preview: a /random draw. Prefer the chat-type value, then text fallbacks.
                int? rv = roll ?? RollValueFromText(text);
                if (rv is >= 1 and <= 13)
                {
                    if (!manual && !SenderIsDealer(sender) && !string.IsNullOrEmpty(sender))
                    { Dbg($"draw from '{sender}' != dealer '{Dealer()}'"); return; }
                    if (NameIs(dealingTo, me))
                    {
                        hand.Add(new Card(RankFromRandom(rv.Value), Suits[hand.Count % 4]));
                        totalMode = false; filledFromChat = true;
                        Dbg($"preview draw {RankFromRandom(rv.Value)}");
                    }
                    // The up card is the dealer's first draw; later ones are the reveal, not the up card.
                    else if (dealingTo == "Dealer" && dealer == null)
                    {
                        dealer = new Card(RankFromRandom(rv.Value), '♠');
                        Dbg($"dealer up {dealer.Value.Rank}");
                    }
                    else Dbg($"draw {RankFromRandom(rv.Value)} -> {dealingTo}, not you");
                    return;
                }

                // Bare number announcing the hand just dealt ("15", "1 or 11").
                var bare = BareTotalRx.Match(text);
                if (bare.Success)
                {
                    if (!manual && !SenderIsDealer(sender)) return;
                    ApplyBareTotal(bare, me);
                    return;
                }
            }

            // Preview fallback: "<me>'s hand is N" summary (used only if no cards were captured).
            if (mentionsHand && !text.Contains("your hand", oic))
            {
                var sum = SummaryRx.Match(text);
                if (sum.Success && NameIs(sum.Groups[1].Value, me))
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

            // Hand-line trigger: literal "your hand", or the decision prompt.
            bool handLine = text.Contains("your hand", oic)
                            || (text.Contains("hit", oic) && text.Contains("stand", oic));
            if (!handLine) return;

            // Sender must be the dealer (auto-locked or configured), unless forced.
            if (!manual && !SenderIsDealer(sender))
            {
                Dbg($"skip: sender '{sender}' != dealer '{Dealer()}'");
                return;
            }

            // Ownership: name prefix wins, else current turn.
            bool mine = manual || (prompt.Success ? NameIs(prompt.Groups[1].Value, me) : myTurn);
            if (!mine) { Dbg("skip: not my hand"); return; }

            ParseAndFill(text);
        }

        // A dealer that prints a hand as a bare number on the line after the draws. The announced
        // total is authoritative: cards that disagree with it mean a draw line was missed.
        private void ApplyBareTotal(Match m, string me)
        {
            bool soft = m.Groups[4].Success;
            int n = int.Parse(soft ? m.Groups[4].Value : m.Groups[1].Value);
            // "14 or 7/7 splits" — the dealer is offering the split, so the hand is that pair.
            bool pair = m.Groups[2].Success && m.Groups[2].Value == m.Groups[3].Value;

            if (dealingTo == "Dealer")
            {
                if (dealer == null && n is >= 1 and <= 11)
                {
                    dealer = new Card(n is 1 or 11 ? "A" : n.ToString(), '♠');
                    Dbg($"dealer up {dealer.Value.Rank} (announced)");
                }
                return;
            }

            if (dealingTo == null || !NameIs(dealingTo, me) || n is < 2 or > 21) return;

            if (hand.Count > 0 && !pair)
            {
                int high = HandTotal(hand, out bool isSoft);
                if (n == high || n == (isSoft ? high - 10 : high)) return;
                Dbg($"cards total {high}, dealer said {n} -> total mode");
            }
            inTotal = n; inSoft = soft; inPair = pair;
            totalMode = true; filledFromChat = true;
        }

        // Chat rarely shows the name the object table holds. The client's name-display setting
        // abbreviates either half ("Hina Reizei" reaches chat as "H. Reizei", "Hina R." or "H. R."),
        // and a cross-world player carries a world icon and home world after it. So a chat rendering
        // is matched against every form the character's real name can take, by prefix.
        private static bool NameIs(string candidate, string me)
        {
            candidate = CleanName(candidate);
            if (candidate.Length == 0 || string.IsNullOrEmpty(me)) return false;
            foreach (var form in NameForms(me))
                if (candidate.StartsWith(form, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether a line names the given character in any of its chat renderings.</summary>
        private static bool NameMentioned(string text, string me)
        {
            foreach (var form in NameForms(me))
                if (text.Contains(form, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static IEnumerable<string> NameForms(string me)
        {
            yield return me;
            int sp = me.IndexOf(' ');
            if (sp <= 0 || sp >= me.Length - 1) yield break;
            string first = me[..sp], last = me[(sp + 1)..];
            yield return $"{first} {last[0]}.";
            yield return $"{first[0]}. {last}";
            yield return $"{first[0]}. {last[0]}.";
        }

        // Dealers write words in the game's boxed letters (U+E071-U+E08A = A-Z), so "the DEALER's
        // first Card" reaches a plugin as six private-use glyphs where the word should be. Read them
        // back as letters before anything else looks at the line; the remaining private-use
        // characters (job and world icons) are decoration and are dropped by CleanName.
        private static string Deglyph(string s)
        {
            char[]? a = null;
            for (int i = 0; i < s.Length; i++)
                if (s[i] is >= '\uE071' and <= '\uE08A')
                {
                    a ??= s.ToCharArray();
                    a[i] = (char)('A' + (s[i] - '\uE071'));
                }
            return a == null ? s : new string(a);
        }

        private static string CleanName(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                if (ch is < '\uE000' or > '\uF8FF') sb.Append(ch);   // private-use area: world/job icons
            return sb.ToString().Trim(' ', '\t', '!', '?', '*', '=', '-', ',', ':', '★', '☆');
        }

        // "below 16 and rolls again" is a threshold: they draw under it and stand on it, soft or not.
        // "stands on 17" names the same threshold from the other side.
        private void ReadHouseRule(string text)
        {
            var m = HouseDrawsRx.Match(text);
            bool hitsSoft = false;
            if (!m.Success)
            {
                m = HouseStandsRx.Match(text);
                hitsSoft = m.Success && text.Contains("hits soft", StringComparison.OrdinalIgnoreCase);
            }
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n) || n is < 12 or > 21) return;
            if (houseStandsOn == n && houseHitsSoft == hitsSoft) return;
            houseStandsOn = n;
            houseHitsSoft = hitsSoft;
            Dbg($"house rule announced: stands on {n}{(hitsSoft ? ", hits soft" : "")}");
        }

        public (int Total, bool HitsSoft)? AnnouncedHouseRule =>
            houseStandsOn is { } n && (n != c.DealerStandsOn || houseHitsSoft != c.DealerHitsSoft17)
                ? (n, houseHitsSoft) : null;

        private string Dealer() => !string.IsNullOrWhiteSpace(c.DealerName) ? c.DealerName : dealerSender ?? "";

        private bool SenderIsDealer(string sender)
        {
            if (!string.IsNullOrWhiteSpace(c.DealerName))
                return CleanName(sender).StartsWith(CleanName(c.DealerName), StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(dealerSender)) return true; // not locked yet -> allow bootstrap
            return SameSpeaker(sender, dealerSender);
        }

        // One speaker reaches chat under more than one rendering: a /random result and a party line
        // carry different sender payloads for the same person — the world suffix and the job icon
        // come and go, and a system-typed line carries no sender at all. Compare on the name.
        private static bool SameSpeaker(string a, string b)
        {
            a = CleanName(a);
            b = CleanName(b);
            if (a.Length == 0 || b.Length == 0) return false;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
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
