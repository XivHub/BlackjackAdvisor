# Blackjack Advisor

Dalamud plugin: optimal-play advisor + gil tracker for FFXIV player-run blackjack (the `/random 1-13`
variant where 10/J/Q/K = 10 and Ace = 1/11).

Because every draw is an independent uniform `/random 1-13` with nothing removed, the card *value*
distribution is exactly an infinite-deck shoe (A=1/13, 2-9=1/13, 10-value=4/13). So the best move is
computed by **exact expected value** — no strategy tables, no card counting, deck size irrelevant.
The engine (`Strategy/BlackjackEngine.cs`) is validated against published H17 dealer bust rates and
Wizard of Odds infinite-deck EVs.

## Features
- Enter your hand with card buttons (or a bare total for dealers that only announce numbers).
- Shows the recommended move, the EV of every option, and a one-line reason.
- Configurable rules: dealer H17/S17, double-after-split, and whether the host offers double/split.
- One-click buttons to say your move in chat — **only when you press them, never automatic**.
- Live gil session tracker (net since session start, read from inventory) + manual W/L/P.
- Auto-fills your hand + dealer up card from the dealer's chat on your turn (toggle in Rules).

## Build
```
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build BlackjackAdvisor/BlackjackAdvisor.csproj -c Release -p:Platform=x64
```
Deploy: bump `<Version>`, then `publish-plugin` (or `./publish.sh`). Command: `/bj`.

## Needs in-game verification (compiles ≠ works)
- `IChatGui.ChatMessage` handler fires and `Message.TextValue` contains the card glyphs (♣♠♦♥).
- Chat auto-fill: ownership gating (turn header / name-prefix vs `ObjectTable.LocalPlayer.Name`),
  glyph parsing, and the total-only fallback, against **real** dealer macros (they vary).
- `ECommons.Automation.Chat.SendMessage` actually posts to the chosen channel.
- Gil reads correctly via `InventoryManager.Instance()->GetInventoryItemCount(1)`.
- ImGui layout/readability in the live client.

## Auto-fill limitation
If a dealer prints neither turn headers (`... 's Turn ...`) nor name-prefixed hand lines
(`<Name>, your hand is ...` / `<Name>, would you like to ...`) nor per-player deal lines, a generic
"Your Hand is:" line can't be attributed to you, so it won't auto-fill. Manual entry always works. See `chat-samples.md` for the formats handled.
