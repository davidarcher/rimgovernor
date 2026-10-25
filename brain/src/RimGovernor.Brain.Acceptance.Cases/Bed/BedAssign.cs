using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>bed/assign</c>: the AssignIntent on Actions/Apply drives the native CompAssignableToPawn.TryAssignPawn write the
/// Assign tab uses. A roofed room holds three staged beds: the sleeper owns one, a second colonist owns another and the
/// third is vacant. Assigning the sleeper to the vacant bed moves its ownership there; assigning it, flagged as a swap, to
/// the other colonist's bed evicts that owner. Both are read back through the production pawn read. First an assign
/// whose expected previous bed has drifted is refused natively and changes nothing.
/// </summary>
internal static class BedAssign
{
    internal static TypedCase Case() => new()
    {
        Name = "bed/assign",
        Scope = "Native CompAssignableToPawn.TryAssignPawn through the AssignIntent on Actions/Apply: a colonist who owns one bed "
            + "is reassigned to a vacant compliant bed, then, flagged as a swap, to another colonist's bed, evicting that owner; "
            + "each ownership change is read back natively, not taken from the result. An assign whose expected previous bed has "
            + "drifted is refused and changes nothing.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 8,
                Height = 5,
                OffsetX = 4,
                Radius = 12,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
            },
            Rooms = [new Fx.StageRoom { Name = "bedroom", Rect = new(0, 0, 8, 5), Stuff = "WoodLog", Doors = [new(0, 2)], Roof = true }],
            Buildings =
            [
                new Fx.StageBuilding { Name = "previous", Def = "Bed", Stuff = "WoodLog", Cells = [new(1, 1)] },
                new Fx.StageBuilding { Name = "vacant", Def = "Bed", Stuff = "WoodLog", Cells = [new(3, 1)] },
                new Fx.StageBuilding { Name = "held", Def = "Bed", Stuff = "WoodLog", Cells = [new(5, 1)] },
            ],
            Pawns =
            [
                new Fx.StagePawn { Name = "sleeper", Kind = "Colonist", Cell = new(2, 3), Bed = "previous" },
                new Fx.StagePawn { Name = "holder", Kind = "Colonist", Cell = new(4, 3), Bed = "held" },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var sleeper = staged.Pawns["sleeper"].Id;
        var holder = staged.Pawns["holder"].Id;
        var previous = staged.Buildings["previous"].Ids.Single();
        var vacant = staged.Buildings["vacant"].Ids.Single();
        var held = staged.Buildings["held"].Ids.Single();
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task OwnsAsync(string label, string pawn, string bed, bool owns)
        {
            var row = await TypedPawnOps.PawnAsync(s, label, pawn, cancellationToken).ConfigureAwait(false)
                ?? throw new AcceptanceException($"{label}: no row for {pawn}");
            s.RecordWire(label, row);
            if ((row.OwnedBed?.Id == bed) != owns)
            {
                throw new AcceptanceException($"{label}: {pawn} owns \"{row.OwnedBed?.Id}\", want {(owns ? "" : "not ")}{bed}");
            }
        }

        static Operations.ActionIntent Assign(string pawn, string bed, string expected, bool swap) => new Operations.AssignIntent
        {
            PawnId = pawn,
            ThingId = bed,
            ExpectedPrevious = new Operations.Assignment { Value = new Operations.AssignmentValue.EntityIdCase(expected) },
            Swap = swap,
        };

        await OwnsAsync("sleeper-before", sleeper, previous, owns: true).ConfigureAwait(false);
        await OwnsAsync("holder-before", holder, held, owns: true).ConfigureAwait(false);

        await TypedPawnOps.RefusedAsync(s, "assign-drifted", Assign(sleeper, vacant, held, swap: false), "Previous assignment changed", cancellationToken).ConfigureAwait(false);
        await OwnsAsync("sleeper-kept", sleeper, previous, owns: true).ConfigureAwait(false);

        _ = await TypedPawnOps.ApplyAsync(s, "assign-vacant", Assign(sleeper, vacant, previous, swap: false), cancellationToken).ConfigureAwait(false);
        await OwnsAsync("sleeper-reassigned", sleeper, vacant, owns: true).ConfigureAwait(false);

        _ = await TypedPawnOps.ApplyAsync(s, "assign-swap", Assign(sleeper, held, vacant, swap: true), cancellationToken).ConfigureAwait(false);
        await OwnsAsync("sleeper-swapped", sleeper, held, owns: true).ConfigureAwait(false);
        await OwnsAsync("holder-evicted", holder, held, owns: false).ConfigureAwait(false);
    }
}
