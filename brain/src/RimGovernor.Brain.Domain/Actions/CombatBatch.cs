namespace RimGovernor.Brain.Domain;

/// <summary>
/// A native combat order. Policy reasons are deliberately not part of this command: only the immutable
/// dispatched effect is journaled.
/// </summary>
public sealed record CombatCommand
{
    public string Kind { get; init; } = "";

    public PawnId Pawn { get; init; }

    public PawnId Target { get; init; }

    public Cell Cell { get; init; }

    public Cell Aim { get; init; }

    public string Door { get; init; } = "";

    public string FireMode { get; init; } = "";

    public bool Clear { get; init; }

    public string Shell { get; init; } = "";

    public string Drug { get; init; } = "";
}

/// <summary>
/// Ordered commands, immutable and comparable. Fight is the session fight plan; Key identifies this exact
/// tactical decision.
/// </summary>
public sealed record CombatBatch : IActionPayload
{
    public CombatBatch(PlanId fight, string key, IReadOnlyList<CombatCommand> orders)
    {
        DomainException.Require(fight.IsValid && Identity.IsValidText(key) && orders.Count > 0, "invalid combat batch");
        foreach (var o in orders)
        {
            DomainException.Require(o.Cell.X >= 0 && o.Cell.Z >= 0 && o.Aim.X >= 0 && o.Aim.Z >= 0, "invalid combat cell");
            switch (o.Kind)
            {
                case "door":
                case "mortar_fire":
                    DomainException.Require(o.Pawn.IsEmpty, "combat structure order names pawn");
                    break;
                case "draft":
                case "move":
                case "attack":
                case "rescue":
                case "man_mortar":
                case "attack_ground":
                case "repair":
                case "stop":
                case "hold_position":
                case "release":
                case "animal_area":
                case "animal_clear":
                case "fire_mode":
                case "drug":
                    DomainException.Require(o.Pawn.IsValid, "combat pawn missing");
                    break;
                default:
                    throw new DomainException("unsupported combat command");
            }
            if (o.Kind is "attack" or "rescue" or "release")
            {
                DomainException.Require(o.Target.IsValid && o.Target != o.Pawn, "invalid combat target");
            }
            if (o.Kind == "door")
            {
                DomainException.Require(o.Door is "allow" or "forbid" or "hold_open" or "close", "invalid combat door mode");
            }
            if (o.Kind == "fire_mode")
            {
                DomainException.Require(o.FireMode is "fire_at_will" or "hold_fire", "invalid combat fire mode");
            }
            if (o.Kind == "drug")
            {
                DomainException.Require(Identity.IsValidText(o.Drug), "invalid combat drug");
            }
        }
        Fight = fight;
        Key = key;
        Orders = EquatableArray.From(orders);
    }

    public PlanId Fight { get; }

    public string Key { get; }

    public EquatableArray<CombatCommand> Orders { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCombatBatchAction(ActionId id, CombatBatch b) => Create(id, ActionKind.CombatBatch, b);

    public CombatBatch? CombatBatch => PayloadOf<CombatBatch>(ActionKind.CombatBatch);
}

/// <summary>One native result. Missing results remain unknown.</summary>
public sealed record CombatResult
{
    public int Index { get; init; }

    /// <summary>Go <c>PawnID</c> (a plain string here).</summary>
    public string PawnId { get; init; } = "";

    public bool Applied { get; init; }

    public string Refusal { get; init; } = "";

    public string JobDef { get; init; } = "";
}

/// <summary>The recorded combat results, held by value in a fact on the progress view.</summary>
public sealed record CombatResults(EquatableArray<CombatResult> Orders);

public sealed partial record Progress
{
    public Progress RecordCombatReceipt(AttemptId attempt, Receipt receipt, IReadOnlyList<CombatResult> results)
    {
        var b = Action.CombatBatch ?? throw new DomainException("not a combat action");
        if (receipt == Receipt.Accepted && results.Count != b.Orders.Count)
        {
            throw new DomainException("combat results missing");
        }
        if (receipt != Receipt.Accepted && results.Count != 0)
        {
            throw new DomainException("combat results without applied receipt");
        }
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.Index != i || r.PawnId != b.Orders[i].Pawn.Value || (r.Applied && r.Refusal != "") || (!r.Applied && r.Refusal == ""))
            {
                throw new DomainException("invalid combat result");
            }
        }
        var next = RecordReceiptCore(attempt, receipt);
        if (receipt == Receipt.Accepted)
        {
            return next.With(v => v with
            {
                Combat = Known(new CombatResults(EquatableArray.From(results))),
                Unresolved = false,
                Stage = v.Stage != Stage.Cancelled ? Stage.Completed : v.Stage,
                Effect = Known(Effect.Completed),
            });
        }
        if (receipt == Receipt.Refused)
        {
            return next.With(v => v with
            {
                Unresolved = false,
                Stage = Stage.Unsuccessful,
                Effect = Known(Effect.Absent),
            });
        }
        return next;
    }
}
