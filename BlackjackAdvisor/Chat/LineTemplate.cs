using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackjackAdvisor.Chat
{
    /// <summary>Turns a dealer's chat line into a wording template — the words that never change,
    /// with the name and number the dealer improvises each time reduced to slots — so the same
    /// macro is recognised however it addresses whichever player.</summary>
    public static class LineTemplate
    {
        // Job/world icons glued to a name, and any other decoration in the private-use area. The
        // boxed A-Z letters (U+E071-U+E08A) are decoded to text by Deglyph before this ever runs.
        private const char PuaAreaStart = (char)0xE000;
        private const char PuaAreaEnd = (char)0xF8FF;

        private const string NameToken = "<name>";
        private const string NumToken = "<n>";
        private static readonly char[] SuitGlyphs = { '♣', '♠', '♦', '♥' };

        private static readonly Regex SlotTokenRx = new(@"<name>|<n>", RegexOptions.Compiled);

        /// <summary>Reduces raw chat text to the form templates are matched on: decoded letters,
        /// lowercase, punctuation flattened to spaces, whitespace collapsed.
        /// " Here is your first two Cards Hina R.!" -&gt; "here is your first two cards hina r".</summary>
        public static string Canon(string raw)
        {
            string s = ChatText.Deglyph(raw);

            var noPua = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (ch < PuaAreaStart || ch > PuaAreaEnd) noPua.Append(ch);
            s = ChatText.NormalizeDigits(noPua.ToString()).ToLowerInvariant();

            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                sb.Append(char.IsLetterOrDigit(ch) || ch == ' ' || Array.IndexOf(SuitGlyphs, ch) >= 0 ? ch : ' ');

            var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words);
        }

        /// <summary>Replaces every known name (longest form first, on word boundaries) with
        /// &lt;name&gt;, then every maximal run of digits/suit glyphs with &lt;n&gt;. Returns the
        /// resulting template and the literal text each slot stood for, in the order they occur.</summary>
        public static (string Template, IReadOnlyList<string> Slots) Templatize(string canon, IEnumerable<string> knownNames)
        {
            var words = canon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var nameForms = knownNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(w => w.Length > 0)
                .OrderByDescending(w => w.Length)
                .ThenByDescending(w => string.Join(' ', w).Length)
                .ToList();

            var output = new List<string>();
            var slots = new List<string>();
            int i = 0;
            while (i < words.Length)
            {
                bool matchedName = false;
                foreach (var form in nameForms)
                {
                    if (i + form.Length > words.Length) continue;
                    bool same = true;
                    for (int k = 0; k < form.Length; k++)
                        if (!string.Equals(words[i + k], form[k], StringComparison.Ordinal)) { same = false; break; }
                    if (!same) continue;

                    output.Add(NameToken);
                    slots.Add(string.Join(' ', form));
                    i += form.Length;
                    matchedName = true;
                    break;
                }
                if (matchedName) continue;

                string word = words[i];
                if (IsNumericRun(word))
                {
                    output.Add(NumToken);
                    slots.Add(word);
                }
                else output.Add(word);
                i++;
            }

            return (string.Join(' ', output), slots);
        }

        private static bool IsNumericRun(string word)
        {
            foreach (char ch in word)
                if (!char.IsDigit(ch) && Array.IndexOf(SuitGlyphs, ch) < 0) return false;
            return word.Length > 0;
        }

        /// <summary>Whether each templatized slot, in order, is a &lt;name&gt; (true) or &lt;n&gt;
        /// (false) — needed to interpret the groups a <see cref="Matcher"/> match produces.</summary>
        public static IReadOnlyList<bool> SlotKinds(string template)
        {
            var kinds = new List<bool>();
            foreach (Match m in SlotTokenRx.Matches(template)) kinds.Add(m.Value == NameToken);
            return kinds;
        }

        /// <summary>Length of the template's literal wording alone, slots excluded — how
        /// dealer-specific a match on it is, used to prefer the most specific template that fits.</summary>
        public static int LiteralLength(string template) => SlotTokenRx.Replace(template, "").Length;

        /// <summary>Builds the anchored, timeout-guarded regex a template is matched with.
        /// &lt;name&gt; captures lazily as any text; &lt;n&gt; captures a run of digits/suit glyphs
        /// that may absorb interior spaces, so "1 or 11" still resolves to one number per slot.</summary>
        public static Regex Matcher(string template)
        {
            var sb = new StringBuilder("^");
            int last = 0;
            foreach (Match m in SlotTokenRx.Matches(template))
            {
                sb.Append(Regex.Escape(template[last..m.Index]));
                sb.Append(m.Value == NameToken ? "(.+?)" : @"([\d♣♠♦♥][\d♣♠♦♥\s]*?)");
                last = m.Index + m.Length;
            }
            sb.Append(Regex.Escape(template[last..]));
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
        }

        /// <summary>A template too generic to bind: the wording that is not a slot must carry at
        /// least 3 words and 10 letters, or every short dealer line would match it.</summary>
        public static bool IsTooGeneral(string template)
        {
            string literal = SlotTokenRx.Replace(template, " ");
            var words = literal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int letters = literal.Count(char.IsLetter);
            return words.Length < 3 || letters < 10;
        }

        /// <summary>Whether a captured &lt;name&gt; slot still looks like a name rather than a
        /// number, a total, or a run-on sentence the template failed to anchor correctly.</summary>
        public static bool SlotIsNameShaped(string slot)
        {
            if (slot.Length > 40) return false;
            if (slot.Any(char.IsDigit)) return false;
            int words = slot.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return words <= 4;
        }

        // Runs the exact word-splitting half of Canon (everything but the final lowercase), but
        // keeps, for each resulting word, the original raw substring that produced it — including
        // punctuation Canon itself throws away, such as the period in "K.". Deglyph and
        // NormalizeDigits are both length- and position-preserving, so only the PUA strip needs an
        // explicit index map back to raw.
        private static (string[] CanonWords, string[] RawWords) WordsWithRawSpans(string raw)
        {
            string s1 = ChatText.Deglyph(raw);

            var s2 = new StringBuilder(s1.Length);
            var toRaw = new List<int>(s1.Length);
            for (int i = 0; i < s1.Length; i++)
            {
                if (s1[i] < PuaAreaStart || s1[i] > PuaAreaEnd) { s2.Append(s1[i]); toRaw.Add(i); }
            }

            string s3 = ChatText.NormalizeDigits(s2.ToString());
            var s4 = new StringBuilder(s3.Length);
            foreach (char ch in s3)
                s4.Append(char.IsLetterOrDigit(ch) || ch == ' ' || Array.IndexOf(SuitGlyphs, ch) >= 0 ? ch : ' ');

            var canonWords = new List<string>();
            var rawWords = new List<string>();
            foreach (Match m in Regex.Matches(s4.ToString(), @"\S+"))
            {
                canonWords.Add(m.Value.ToLowerInvariant());
                int rawStart = toRaw[m.Index];
                int rawEnd = toRaw[m.Index + m.Length - 1] + 1;
                rawWords.Add(raw[rawStart..rawEnd]);
            }
            return (canonWords.ToArray(), rawWords.ToArray());
        }

        /// <summary>Maps a slot value captured against canon text back to the dealer's original
        /// wording for it — "Mira K.", not "mira k" — by locating the same run of canon words and
        /// reading off their original raw spans. Falls back to the canon value itself if the run
        /// cannot be found (should not happen: it is built from the very same text).</summary>
        public static string RecoverOriginalCase(string raw, string canonSlot)
        {
            var (canonWords, rawWords) = WordsWithRawSpans(raw);
            var slotWords = canonSlot.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (slotWords.Length == 0) return canonSlot;

            for (int start = 0; start + slotWords.Length <= canonWords.Length; start++)
            {
                bool match = true;
                for (int k = 0; k < slotWords.Length; k++)
                    if (!string.Equals(canonWords[start + k], slotWords[k], StringComparison.Ordinal)) { match = false; break; }
                if (match) return string.Join(' ', rawWords.Skip(start).Take(slotWords.Length));
            }
            return canonSlot;
        }
    }
}
