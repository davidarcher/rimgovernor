namespace RimGovernor.Brain.Domain;

/// <summary>Toggles vanilla autohack; it does not force a hacking job.</summary>
public sealed record HackDesignation : IActionPayload
{
    /// <summary>Go <c>NewHackDesignation</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public HackDesignation(string target, bool enabled)
    {
        DomainException.Require(Identity.IsValidText(target), "hack designation requires an exact target");
        Target = target;
        Enabled = enabled;
    }

    public string Target { get; }

    public bool Enabled { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewHackDesignationAction(ActionId id, HackDesignation hack) => Create(id, ActionKind.HackDesignation, hack);

    public HackDesignation? HackDesignation => PayloadOf<HackDesignation>(ActionKind.HackDesignation);
}
