using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>One row of <c>mechanic_cards_verified.tsv</c>: a card def observed by a native probe at a drift hash.</summary>
internal sealed record VerifiedRow(int Line, string Def, string DriftHash, string Probe, string Game, string Evidence)
{
    public override string ToString() => $"line {Line}: {Def}\t{DriftHash}\t{Probe}\t{Game}\t{Evidence}";
}

/// <summary>
/// The verified-cards table (#3222): a card ships <c>verified</c> only while its current drift hash matches a row that a
/// native probe pass recorded. Pure (no Roslyn, no files) so the domain tests compile it in.
/// </summary>
internal static class VerifiedCards
{
    public const string Header = "def\tdrift_hash\tprobe\tgame\tevidence";

    /// <summary>
    /// A card's drift hash: the first 16 hex digits of SHA-256 over its members' body hashes (every grant site and every
    /// writer, follows included), distinct, sorted ordinally and joined by newlines. Any change to one of those methods changes it.
    /// </summary>
    public static string DriftHash(IEnumerable<string> memberHashes)
    {
        var joined = string.Join('\n', memberHashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Parses the table; throws <see cref="InvalidDataException"/> on a bad header, a malformed row, a def absent from
    /// <paramref name="cardDefs"/> or a def in <paramref name="unreachable"/> (#3243: a card whose guard can never hold has no
    /// positive path for a probe to verify).
    /// </summary>
    public static IReadOnlyList<VerifiedRow> Parse(string tsv, IReadOnlySet<string> cardDefs, IReadOnlySet<string>? unreachable = null)
    {
        var lines = tsv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (tsv.Length == 0)
        {
            return [];
        }
        if (lines[0] != Header)
        {
            throw new InvalidDataException($"line 1: header must be \"{Header.Replace("\t", "\\t", StringComparison.Ordinal)}\"");
        }
        var rows = new List<VerifiedRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }
            var f = lines[i].Split('\t');
            if (f.Length != 5 || f.Any(x => x.Trim().Length == 0))
            {
                throw new InvalidDataException($"line {i + 1}: want 5 non-empty tab-separated fields: {lines[i]}");
            }
            if (!cardDefs.Contains(f[0]))
            {
                throw new InvalidDataException($"line {i + 1}: {f[0]} is not a card in the card file");
            }
            if (unreachable?.Contains(f[0]) == true)
            {
                throw new InvalidDataException($"line {i + 1}: {f[0]} is unreachable and cannot be verified");
            }
            rows.Add(new VerifiedRow(i + 1, f[0], f[1], f[2], f[3], f[4]));
        }
        return rows;
    }

    /// <summary>Whether <paramref name="def"/> is verified at <paramref name="driftHash"/>; when it is not, the def's rows recorded at another hash (stale, to re-probe).</summary>
    public static (bool Verified, IReadOnlyList<VerifiedRow> Stale) Match(string def, string driftHash, IReadOnlyList<VerifiedRow> rows)
    {
        var mine = rows.Where(r => r.Def == def).ToList();
        return mine.Any(r => r.DriftHash == driftHash) ? (true, []) : (false, mine);
    }
}
