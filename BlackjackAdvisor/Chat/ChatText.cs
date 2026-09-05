using System.Collections.Generic;
using System.Text;

namespace BlackjackAdvisor.Chat
{
    /// <summary>Name and glyph handling shared by every dealer-format parser. No dependency on
    /// Dalamud: every input here is a plain string already pulled out of a chat message.</summary>
    public static class ChatText
    {
        // The game's boxed-letter glyphs (A-Z) and the private-use icon range they sit inside.
        private const char PuaGlyphStart = (char)0xE071;
        private const char PuaGlyphEnd = (char)0xE08A;
        private const char PuaAreaStart = (char)0xE000;
        private const char PuaAreaEnd = (char)0xF8FF;
        private const char FullwidthZero = (char)0xFF10;
        private const char FullwidthNine = (char)0xFF19;

        // Dealers write words in the game's boxed letters (U+E071-U+E08A = A-Z), so "the DEALER's
        // first Card" reaches a plugin as six private-use glyphs where the word should be. Read them
        // back as letters before anything else looks at the line; the remaining private-use
        // characters (job and world icons) are decoration and are dropped by CleanName.
        public static string Deglyph(string s)
        {
            char[]? a = null;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= PuaGlyphStart && s[i] <= PuaGlyphEnd)
                {
                    a ??= s.ToCharArray();
                    a[i] = (char)('A' + (s[i] - PuaGlyphStart));
                }
            return a == null ? s : new string(a);
        }

        public static string CleanName(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (ch < PuaAreaStart || ch > PuaAreaEnd) sb.Append(ch);   // private-use area: world/job icons
            return StripJobPrefix(sb.ToString()).Trim(' ', '\t', '!', '?', '*', '=', '-', ',', ':', '★', '☆', '"');
        }

        // The sender payload keeps a job abbreviation glued to the name once the job icon itself
        // is stripped ("<icon>AST Lina C.Odin" -> "AST Lina C.Odin"): a run of 2-4 uppercase ASCII
        // letters followed by a space, with at least two characters of name left over.
        public static string StripJobPrefix(string sender)
        {
            int i = 0;
            while (i < sender.Length && sender[i] is >= 'A' and <= 'Z') i++;
            if (i is < 2 or > 4 || i >= sender.Length || sender[i] != ' ') return sender;
            string rest = sender[(i + 1)..];
            return rest.Length >= 2 ? rest : sender;
        }

        // Chat rarely shows the name the object table holds. The client's name-display setting
        // abbreviates either half ("Hina Reizei" reaches chat as "H. Reizei", "Hina R." or "H. R."),
        // and a cross-world player carries a world icon and home world after it. So a chat rendering
        // is matched against every form the character's real name can take, by prefix.
        public static bool NameIs(string candidate, string me)
        {
            candidate = CleanName(candidate);
            if (candidate.Length == 0 || string.IsNullOrEmpty(me)) return false;
            foreach (var form in NameForms(me))
                if (candidate.StartsWith(form, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether a line names the given character in any of its chat renderings.</summary>
        public static bool NameMentioned(string text, string me)
        {
            foreach (var form in NameForms(me))
                if (text.Contains(form, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static IEnumerable<string> NameForms(string me)
        {
            yield return me;
            int sp = me.IndexOf(' ');
            if (sp <= 0 || sp >= me.Length - 1) yield break;
            string first = me[..sp], last = me[(sp + 1)..];
            yield return $"{first} {last[0]}.";
            yield return $"{first[0]}. {last}";
            yield return $"{first[0]}. {last[0]}.";
        }

        // One speaker reaches chat under more than one rendering: a /random result and a party line
        // carry different sender payloads for the same person — the world suffix and the job icon
        // come and go, and a system-typed line carries no sender at all. Compare on the name.
        public static bool SameSpeaker(string a, string b)
        {
            a = CleanName(a);
            b = CleanName(b);
            if (a.Length == 0 || b.Length == 0) return false;
            return a.StartsWith(b, System.StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a, System.StringComparison.OrdinalIgnoreCase);
        }

        // Tolerant of the JP client's fullwidth digits.
        public static string NormalizeDigits(string s)
        {
            char[]? a = null;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= FullwidthZero && s[i] <= FullwidthNine)
                {
                    a ??= s.ToCharArray();
                    a[i] = (char)('0' + (s[i] - FullwidthZero));
                }
            return a == null ? s : new string(a);
        }
    }
}
