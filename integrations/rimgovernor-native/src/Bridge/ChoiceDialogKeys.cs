#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Language-neutral names for a choice dialog's options (#179). RimWorld
    // translates an option's key before the DiaOption is built ("OK".Translate(),
    // "CaravanDemand_Give".Translate()) and keeps only the text, so the census
    // recovers the key by reverse lookup over the loaded Keyed translations:
    // every key of the active language or of English whose text (or template,
    // for a value with {0}-style arguments) produces the label. Several keys can
    // share one text ("OK" and "GoodCondition"), so the census reports all of
    // them and the controller's policy names the key it means.
    internal static class ChoiceDialogKeys
    {
        private const int MaxCached = 256;
        private static readonly object Gate = new object();
        private static LoadedLanguage? active, fallback;
        private static Dictionary<string, List<string>> exact = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static List<KeyValuePair<Regex, string>> templates = new List<KeyValuePair<Regex, string>>();
        private static readonly Dictionary<string, List<string>> cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        internal static IReadOnlyList<string> ForLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return Array.Empty<string>();
            lock (Gate)
            {
                Refresh();
                if (cache.TryGetValue(label, out var hit)) return hit;
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                if (exact.TryGetValue(label, out var direct)) keys.UnionWith(direct);
                foreach (var template in templates) if (template.Key.IsMatch(label)) keys.Add(template.Value);
                var result = keys.ToList();
                if (cache.Count >= MaxCached) cache.Clear();
                cache[label] = result;
                return result;
            }
        }

        private static void Refresh()
        {
            var current = LanguageDatabase.activeLanguage;
            var english = LanguageDatabase.defaultLanguage;
            if (ReferenceEquals(current, active) && ReferenceEquals(english, fallback) && (exact.Count > 0 || templates.Count > 0)) return;
            active = current; fallback = english;
            exact = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            templates = new List<KeyValuePair<Regex, string>>();
            cache.Clear();
            foreach (var language in new[] { current, english })
            {
                if (language?.keyedReplacements == null) continue;
                foreach (var entry in language.keyedReplacements)
                {
                    var replacement = entry.Value;
                    if (replacement == null || replacement.isPlaceholder || string.IsNullOrEmpty(replacement.value)) continue;
                    var text = Normalize(replacement.value);
                    if (text.Length == 0) continue;
                    if (text.IndexOf('{') < 0)
                    {
                        if (!exact.TryGetValue(text, out var list)) exact[text] = list = new List<string>();
                        if (!list.Contains(entry.Key)) list.Add(entry.Key);
                        continue;
                    }
                    // A template names a label only through its literal text:
                    // "{0} {1}" would match any two words.
                    if (Regex.Replace(text, @"\{[^}]*}", "").Count(char.IsLetter) < 3 || templates.Any(t => t.Value == entry.Key)) continue;
                    var pattern = Regex.Replace(Regex.Escape(text), @"\\\{[^}]*}", ".+?");
                    templates.Add(new KeyValuePair<Regex, string>(new Regex("^" + pattern + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant), entry.Key));
                }
            }
        }

        // The same projection ChoiceDialogTools applies to an option's text:
        // tags stripped, so a coloured translation still matches its label.
        private static string Normalize(string value) => value.StripTags().Trim();
    }
}
