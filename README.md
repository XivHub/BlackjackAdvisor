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
- Configurable rules: the total the dealer stands on (17 in a casino, some hosts stop at 16),
  whether a soft one is hit, double-after-split, and whether the host offers double/split.
- One-click buttons to say your move in chat — **only when you press them, never automatic**.
- Live gil session tracker (net since session start, read from inventory) + manual W/L/P.
- Auto-fills your hand + dealer up card from the dealer's chat on your turn (toggle in Rules).
- Learns a new venue's wording on its own from the totals the dealer announces, and can be taught
  a line directly from a banner over the table when it can't (see "Teaching a new dealer" below).

## Build
```
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build BlackjackAdvisor/BlackjackAdvisor.csproj -c Release -p:Platform=x64
```
Deploy: bump `<Version>`, then `publish-plugin` (or `./publish.sh`). Commands: `/bj`, `/bj status`
(what the parser currently sees), `/bj parse` (force-read the last chat line).

Turning on "Send the parser trace to a dev log" in Rules and pointing it at
`~/dev/XivHubPluginKit/devlog_server.py` (`http://<box>:9999/log`) records every chat line the parser
saw and what it made of it, so a whole table can be read back afterwards instead of scrolled through
in game chat. It stays dormant with no URL set.

### Parser harness
`tools/ParserHarness` replays a captured dev log through the real `Chat/` parser, outside Dalamud,
and checks it against a sibling `.expect` file:
```
DOTNET_ROOT=~/.dotnet dotnet run --project tools/ParserHarness -- tools/ParserHarness/Fixtures/venue-lina.log
```
`--engine-check` additionally links `Strategy/BlackjackEngine.cs` and checks the default rules
against the known infinite-deck H17 dealer bust rates. `--no-builtins` disables the attribution
regexes below the learned-store lookup (deal/reveal/turn/act/prompt), so a fixture can prove the
checksum learner alone reproduces a venue's hand-filling with zero built-in help. `--dump-trace`
prints every parser trace line to stderr, prefixed `TRACE <fixture>:` — useful for reading back
what the learner concluded and why.

Fixtures under `Fixtures/` are anonymized dev-log captures from real venues — every character name
is replaced before the log is committed, but the abbreviated/cross-world name shapes and the game's
private-use glyphs (boxed letters, job/world icons) are kept byte-for-byte, since those are exactly
what the parser is being tested against. `tools/ParserHarness` is never referenced by
`BlackjackAdvisor.csproj`; it is a standalone offline tool.

A `.expect` file normally shares its capture's name (`venue-lina.log` / `venue-lina.expect`). A
second `.expect` can replay the SAME capture without duplicating it — point the harness at the
`.expect` directly and give it a `#log <relative path>` directive naming the capture to replay
(see `venue-lina.learn.expect`, which replays `venue-lina.log` with `--no-builtins`). `#roster
<name>` (repeatable) supplies the party roster the checksum learner's name-collision guard
resolves an abbreviated subject against — in the running plugin this comes from the object table.

## Needs in-game verification (compiles ≠ works)
- `IChatGui.ChatMessage` handler fires and `Message.TextValue` contains the card glyphs (♣♠♦♥).
- Chat auto-fill: ownership gating (turn header / name-prefix vs `ObjectTable.LocalPlayer.Name`),
  glyph parsing, and the total-only fallback, against **real** dealer macros (they vary).
- `ECommons.Automation.Chat.SendMessage` actually posts to the chosen channel.
- Gil reads correctly via `InventoryManager.Instance()->GetInventoryItemCount(1)`.
- ImGui layout/readability in the live client.

## Auto-fill limitation
Built-in recognition covers the wording in `chat-samples.md`; a venue that phrases things
differently is picked up by the checksum learner instead of a new regex (see "Teaching a new
dealer" below). Auto-fill only fails outright when a dealer's macro gives no turn header, no
name-prefixed hand line, no per-player deal line, *and* no announced total or outcome to check a
hypothesis against — that combination shows up as an "Unclaimed cards" banner over the table.
Manual entry always works regardless.

## Teaching a new dealer
The plugin learns a dealer's wording from arithmetic, not a list of known phrases: it watches which
line precedes a run of `/random` rolls, and checks whether the total the dealer announces next
balances against those rolls under the standard card values (10/J/Q/K=10, A=1/11). Two confirmations
with different rolls bind the line with no input from you; the "· learned N of this dealer's lines"
note under the table says when it happened. Turn it off in Rules -> "Learn this dealer's wording" if
a venue's chat should never be read this way.

When a dealer never announces a total to check against, two banners over the table can still teach
it directly:
- "Unclaimed cards" appears once a run of rolls has nobody to belong to. "Those were my cards" fills
  your hand for that round and offers to bind the line that opened it; "Not mine" drops it.
- Entering your hand with the ordinary card buttons does the same check: if what you clicked matches
  an unclaimed run of rolls, the same offer appears for the line that came before them.

Accepting an offer binds it as a confirmed line, which — unlike the automatic guesses above, which
unbind themselves after three totals in a row that don't balance — the checksum learner never
touches again. "Undo" reverses a binding for 10 seconds after teaching it. Every learned line, for
every dealer, is listed under Rules -> "Learned dealer lines": change what a line means with its
role dropdown, remove one, or forget every line for a dealer and start over.
