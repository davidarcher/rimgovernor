namespace RimGovernor.Brain.Domain;

/// <summary>
/// Native identity validation supplied by the game-aware boundary. Domain owns structural invariants;
/// the boundary binds their game meanings. This service carries no observed or persisted state.
/// </summary>
public interface IGameDefinitionSemantics
{
    bool IsColonyWall(string definition);

    bool IsTradeCurrency(string definition);
}
