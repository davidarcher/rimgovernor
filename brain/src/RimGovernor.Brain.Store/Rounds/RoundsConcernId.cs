using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Names a routine need's Standard by world and need, so every review revision in one world reuses the row. A
    /// live binding is reused before minting, so a mint happens only after an invalidation (tick rewind, need
    /// dropping out); the generation advances past every existing row, so a terminal row is never reopened. A Once
    /// Standard that regresses stays on its row and starts a new Episode instead (Go <c>mintRoundsStandardID</c>).
    /// </summary>
    internal static StandardId MintRoundsStandardId(StoreTransaction tx, World w, ConcernId need)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{w.Colony.Value}/{w.Load.Value}/{w.Map.Value}");
        var prefix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
        for (var gen = 0; gen < 1 << 20; gen++)
        {
            var id = new StandardId(string.Create(CultureInfo.InvariantCulture, $"routine-{prefix}-{need.Value}-{gen}"));
            try
            {
                _ = LoadStandard(tx, id);
            }
            catch (NotFoundException)
            {
                return id;
            }
        }
        throw new CapacityException();
    }
}
