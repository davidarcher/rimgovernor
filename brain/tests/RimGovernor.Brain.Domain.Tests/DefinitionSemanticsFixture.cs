namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Game-free fixtures bind their authored wall and currency identities.</summary>
internal sealed class DefinitionSemanticsFixture : IGameDefinitionSemantics
{
    internal static DefinitionSemanticsFixture Instance { get; } = new();

    public bool IsColonyWall(string definition) => definition == "Wall";

    public bool IsTradeCurrency(string definition) => definition == "Silver";
}
