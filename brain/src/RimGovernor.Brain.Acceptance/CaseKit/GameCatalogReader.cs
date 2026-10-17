using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The harness-side reads of the game's definition catalog (Go <c>cases.Catalog</c> built the typed catalog from the
/// bridge on the harness side). The catalog is now game-side (<c>DefinitionCatalog</c> over the game's Defs), which the
/// out-of-process acceptance harness cannot build, so a case that needs one of its derived values goes through a reader
/// the host wires, as <see cref="ObservedSession.StoreOpener"/> does for the journal. Until the cutover (#3085) assigns a
/// reader, the call fails naming the missing hook.
/// </summary>
public static class GameCatalogReader
{
    /// <summary>
    /// Reads the Impressiveness room stat's score stages of the loaded game (Go <c>Catalog.ImpressivenessLevels</c>):
    /// the minimum scores of the named stages the planners aim at, for the game identified by <c>identity</c>.
    /// </summary>
    public static Func<Harness, JsonObject, CancellationToken, Task<ImpressivenessLevels>>? ImpressivenessReader { get; set; }

    /// <summary>
    /// Reads the terrain table of the loaded game's definition catalog (<c>DefinitionCatalog.FloorTerrains</c>): the
    /// cleanliness and natural flag of every TerrainDef, for the game identified by <c>identity</c>.
    /// </summary>
    public static Func<Harness, JsonObject, CancellationToken, Task<IReadOnlyDictionary<string, FloorTerrainRow>>>? FloorTerrainsReader { get; set; }

    /// <summary>The terrain table of the game identified by <paramref name="identity"/>.</summary>
    public static Task<IReadOnlyDictionary<string, FloorTerrainRow>> FloorTerrainsAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);
        var read = FloorTerrainsReader
            ?? throw new AcceptanceException("no catalog reader is wired for the floor terrains (GameCatalogReader.FloorTerrainsReader): the case cannot read terrain stats");
        return read(harness, identity, cancellationToken);
    }

    /// <summary>The Impressiveness stage scores of the game identified by <paramref name="identity"/>.</summary>
    public static Task<ImpressivenessLevels> ImpressivenessLevelsAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);
        var read = ImpressivenessReader
            ?? throw new AcceptanceException("no catalog reader is wired for the Impressiveness room stat (GameCatalogReader.ImpressivenessReader): the case cannot compute a room's tier target");
        return read(harness, identity, cancellationToken);
    }
}

/// <summary>One terrain of the definition catalog: the cleanliness stat a flooring census scores a cell by, and whether the ground is unbuilt.</summary>
public sealed record FloorTerrainRow(double Cleanliness, bool Natural);
