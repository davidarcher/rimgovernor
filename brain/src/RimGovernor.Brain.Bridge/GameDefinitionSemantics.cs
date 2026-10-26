using RimGovernor.Brain.Domain;
using RimWorld;

namespace RimGovernor.Brain.Bridge;

/// <summary>Compiler-checked native identities at constructor and journal validation boundaries.</summary>
public sealed class GameDefinitionSemantics : IGameDefinitionSemantics
{
    public static GameDefinitionSemantics Instance { get; } = new();

    private GameDefinitionSemantics() { }

    public bool IsColonyWall(string definition) => definition == nameof(ThingDefOf.Wall);

    public bool IsTradeCurrency(string definition) => definition == nameof(ThingDefOf.Silver);
}
