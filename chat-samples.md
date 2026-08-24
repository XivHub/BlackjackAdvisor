# Dealer chat samples (for the auto-fill parser — phase 2)

Note: not all dealers use identical wording. Parser must key on the card glyphs (♣♠♦♥ + rank)
and the words "hand" / "dealer", and track whose turn it is, rather than exact phrasing.

## Card glyphs
Suits: ♣ ♠ ♦ ♥. Rank: A, 2-9, 10, J, Q, K (J/Q/K/10 all = 10; A = 1/11).
Cards drawn via `Random! (1-13) N`.

## The golden line (player's turn — has both hand and dealer up card)
```
Your Hand is: ♣6♣9 - Total: 15. Dealer's Hand: ♣3 - 3. Would you like to hit, stand or double down?
Your Hand is: ♣J♠7 - Total: 17. Dealer's Hand: ♠A - 1 or 11. Would you like to hit, stand or double down?
Your Hand: ♣J♣K - Total: 20. Dealer's Hand: ♠A - 1 or 11. Would you like to hit, stand, double down or split?
```
Note variants: "Your Hand is:" vs "Your Hand:"; action list may include "or split".

## Turn headers (track whose turn -> whose "Your Hand" line it is)
```
==== ★☆  Hina Reizei's Turn ☆★ ====
==== ★☆  Au Tism's Turn ☆★ ====
==== ★☆ Dealer's Turn ☆★ ====
```

## After a hit (name-prefixed; NO dealer card on these)
```
Hina Reizei, your hand is ♠2♦5♠4 - Total: 11. Would you like to hit or stand?
Au Tism, your hand is ♣3♦A♠8 - Total: 12. Would you like to hit or stand?
```

## Stand / bust confirmations
```
Au Tism stays with ♣J♠7 - Total: 17.
Hina Reizei stays with ♠2♦5♠4♦8 - Total: 19.
Au Tism busted with: ♣3♦A♠8♠3♣9 - Total: 24.
```

## Dealer reveal
```
Dealer's hand is 3.
Dealer's hand is 1 or 11.
```
