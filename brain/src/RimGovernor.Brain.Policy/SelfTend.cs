namespace RimGovernor.Brain.Policy;

// Self-tend. Vanilla's WorkGiver_TendSelf is Doctor work gated on playerSettings.selfTend; it tends the pawn
// where it stands, while other doctors only tend a humanlike patient in bed
// (WorkGiver_Tend.GoodLayingStatusForTend), so an up-and-about pawn with self-tend on treats itself before any
// doctor can. TendUtility multiplies a self-tend's quality by SelfTendQualityFactor. The bot turns self-tend
// on when the pawn's own tend quality times that factor is at least the best other available doctor's, or no
// other doctor is available; off otherwise. Doctor priority itself belongs to the work planner.

/// <summary>One colonist's self-tend inputs. TendQuality is the MedicalTendQuality stat, unknown for a pawn that cannot doctor.</summary>
public sealed record SelfTendPawn
{
    public PawnId Id { get; init; }

    public Fact<bool> Current { get; init; }

    public Fact<double> TendQuality { get; init; }

    public Fact<bool> Available { get; init; }

    /// <summary>The row carried its policy inputs, so an unknown TendQuality means the pawn cannot doctor.</summary>
    public bool Read { get; init; }

    /// <summary>A guest or prisoner, never a colony doctor.</summary>
    public bool Guest { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Vanilla TendUtility.SelfTendQualityFactor.</summary>
    public const double SelfTendQualityFactor = 0.7;

    /// <summary>Lifts a work census row into its self-tend inputs.</summary>
    public static SelfTendPawn SelfTendPawn(this WorkPawn w)
    {
        var p = new SelfTendPawn { Id = w.Id, Current = w.SelfTend, Available = w.Available };
        if (w.PolicyInputs.TryGetValue(out var input))
        {
            p = p with
            {
                Read = true,
                TendQuality = input.TendQuality,
                Guest = input.GuestStatus is "Guest" or "Prisoner",
            };
        }
        return p;
    }

    /// <summary>
    /// The self-tend setting pawns[i] should hold; Known is false when a deciding fact is unknown (its own
    /// quality, another colonist's policy inputs, or a doctor's availability). A pawn that cannot doctor has
    /// no setting to decide.
    /// </summary>
    public static (bool Want, bool Known) WantSelfTend(IReadOnlyList<SelfTendPawn> pawns, int i)
    {
        if (!pawns[i].TendQuality.TryGetValue(out var own) || pawns[i].Guest)
        {
            return (false, false);
        }
        own *= SelfTendQualityFactor;
        for (var j = 0; j < pawns.Count; j++)
        {
            var p = pawns[j];
            if (j == i || p.Guest)
            {
                continue;
            }
            if (!p.Read)
            {
                return (false, false);
            }
            if (!p.TendQuality.TryGetValue(out var quality))
            {
                continue;
            }
            if (!p.Available.TryGetValue(out var available))
            {
                return (false, false);
            }
            if (available && quality > own)
            {
                return (false, true);
            }
        }
        return (true, true);
    }

    /// <summary>The self-tend settings to write: every colonist whose known setting differs from the one it should hold.</summary>
    public static List<PawnSettings> SelfTendChanges(IReadOnlyList<SelfTendPawn> pawns)
    {
        var output = new List<PawnSettings>();
        for (var i = 0; i < pawns.Count; i++)
        {
            var p = pawns[i];
            var ck = p.Current.TryGetValue(out var current);
            var (want, ok) = WantSelfTend(pawns, i);
            if (!ck || !ok || current == want)
            {
                continue;
            }
            try
            {
                output.Add(PawnSettings.NewSelfTendSetting(new RimGovernor.Brain.Domain.PawnId(p.Id.Value), want));
            }
            catch (DomainException)
            {
                // Go dropped a setting that failed validation.
            }
        }
        return output;
    }

    /// <summary>The review's SelfTendOwed fact: a change is owed.</summary>
    public static Fact<bool> SelfTendOwed(Fact<EquatableArray<SelfTendPawn>> pawns) =>
        pawns.TryGetValue(out var rows) ? Known(SelfTendChanges(rows).Count > 0) : Unknown<bool>();
}
