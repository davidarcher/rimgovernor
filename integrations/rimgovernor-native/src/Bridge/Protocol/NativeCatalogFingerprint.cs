#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Names the static part of the definition catalog: what decides which defs the
    // game loaded. Defs load once at boot, so the value is computed once per
    // process. It is a cheap stand-in for the rows (game version, language and,
    // per active mod in load order, the id and the write times of the folders
    // that define or patch defs and code), not a hash of them.
    internal static class NativeCatalogFingerprint
    {
        private static readonly string[] Folders = { "Defs", "Patches", "Languages", "Assemblies" };
        private static string? value;

        internal static string Value => value ??= Compute();

        private static string Compute()
        {
            var text = new StringBuilder();
            text.Append(VersionControl.CurrentVersionStringWithRev).Append('\n');
            text.Append(LanguageDatabase.activeLanguage?.folderName).Append('\n');
            foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
            {
                text.Append(mod.PackageId.ToLowerInvariant());
                var root = mod.RootDir?.FullName;
                if (root != null)
                    foreach (var folder in Folders)
                        Append(text, Path.Combine(root, folder));
                text.Append('\n');
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        // The mod's folder at the top level and in each version folder under it:
        // file count and the newest write time in ticks.
        private static void Append(StringBuilder text, string folder)
        {
            var roots = new[] { folder }.Concat(Directory.Exists(Path.GetDirectoryName(folder)!)
                ? Directory.GetDirectories(Path.GetDirectoryName(folder)!).Select(version => Path.Combine(version, Path.GetFileName(folder)))
                : Array.Empty<string>());
            foreach (var root in roots.Where(Directory.Exists))
            {
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                text.Append('|').Append(root.Length).Append(':').Append(files.Length).Append(':')
                    .Append(files.Length == 0 ? 0 : files.Max(file => File.GetLastWriteTimeUtc(file).Ticks));
            }
        }
    }
}
