namespace RimGovernor.Brain.Domain;

/// <summary>
/// A one-shot patch of a temperature-controlled building's target setpoint (CompTempControl on the native
/// side; a BuildingPatchIntent). There is no pawn or Job involved.
/// </summary>
public sealed record BuildingTemperature : IActionPayload
{
    private const double MinTemperatureCelsius = -273.15;
    private const double MaxTemperatureCelsius = 1000;

    /// <summary>Go <c>NewBuildingTemperature</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public BuildingTemperature(string thing, double celsius)
    {
        DomainException.Require(Identity.IsValidText(thing), "invalid building temperature identity");
        DomainException.Require(
            double.IsFinite(celsius) && celsius >= MinTemperatureCelsius && celsius <= MaxTemperatureCelsius,
            "building temperature out of range"
        );
        Thing = thing;
        Celsius = celsius;
    }

    public string Thing { get; }

    public double Celsius { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewBuildingTemperatureAction(ActionId id, BuildingTemperature temperature) =>
        Create(id, ActionKind.BuildingTemperature, temperature);

    /// <summary>Go <c>Action.BuildingTemperature() (BuildingTemperature, bool)</c>: the payload when this is a building temperature action, otherwise null.</summary>
    public BuildingTemperature? BuildingTemperature => PayloadOf<BuildingTemperature>(ActionKind.BuildingTemperature);
}
