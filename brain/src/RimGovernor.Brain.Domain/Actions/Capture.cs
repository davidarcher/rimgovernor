namespace RimGovernor.Brain.Domain;

/// <summary>
/// Brings an observed humanlike into prisoner custody. The ordinary mode uses an undrafted capturer on a
/// downed hostile. Arrest mode names an exact prisoner bed and uses an owned draft on a standing neutral
/// ancient. Both modes require native eligibility and observed custody completion.
/// </summary>
public sealed record Capture : IActionPayload
{
    public Capture(PawnId capturer, PawnId patient)
    {
        DomainException.Require(capturer.IsValid && patient.IsValid && capturer != patient, "capture requires distinct valid capturer and patient identities");
        Capturer = capturer;
        Patient = patient;
    }

    public PawnId Capturer { get; }

    public PawnId Patient { get; }

    /// <summary>The exact prisoner bed in arrest mode; null for an ordinary capture.</summary>
    public string? Bed { get; private init; }

    public bool Arrest => Bed is not null;

    /// <summary>
    /// Captures a standing neutral ancient using the existing native Arrest operation. The plan must first
    /// acquire an owned draft for the capturer.
    /// </summary>
    public static Capture NewArrest(PawnId capturer, PawnId patient, string bed)
    {
        var c = new Capture(capturer, patient);
        DomainException.Require(Identity.IsValidText(bed) && bed != capturer.Value && bed != patient.Value, "arrest requires an exact prisoner bed");
        return c with { Bed = bed };
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCaptureAction(ActionId id, Capture capture) => Create(id, ActionKind.Capture, capture);

    public Capture? Capture => PayloadOf<Capture>(ActionKind.Capture);
}
