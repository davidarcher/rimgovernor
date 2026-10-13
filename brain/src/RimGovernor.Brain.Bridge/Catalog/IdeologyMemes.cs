using RimWorld;

namespace RimGovernor.Brain.Bridge;

// Go catalog.DefRow[*d.MemeDef] as the ideology decode uses it: the host composes the frame's ideology section and cannot
// name the game's MemeDef, so the catalog answers whether it holds the row.
public sealed partial class DefinitionCatalog
{
    /// <summary>Whether the catalog has a meme def of this name (Go <c>DefRow[*d.MemeDef](catalog, name) != nil</c>).</summary>
    public bool HasMeme(string name) => DefRow<MemeDef>(name) is not null;
}
