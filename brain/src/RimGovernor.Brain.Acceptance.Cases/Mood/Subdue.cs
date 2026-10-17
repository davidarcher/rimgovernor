using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/subdue</c>: ordinary melee containment through a GiveJobIntent AttackMelee on Actions/Apply. A Melee-20 colonist
/// is ordered against an unarmed Berserk colonist; the target ends alive, downed or out of the aggressive break, and never
/// a prisoner (the production pawn read).
/// </summary>
internal static class Subdue
{
    internal static TypedCase Case() => new()
    {
        Name = "mood/subdue",
        Scope = "Ordinary melee containment through a GiveJobIntent AttackMelee on Actions/Apply: the ordered colonist contains an "
            + "unarmed Berserk colonist, who ends alive, downed or out of the aggressive break, without prisoner conversion.",
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 6,
                Height = 4,
                OffsetX = 4,
                Radius = 12,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty],
            },
            Pawns =
            [
                new Fx.StagePawn { Name = "subduer", Kind = "Colonist", SkillLevel = 20, ClearTraits = true, Gear = new Fx.StageGear(), Cell = new(0, 1) },
                new Fx.StagePawn
                {
                    Name = "target",
                    Kind = "Colonist",
                    SkillLevel = 0,
                    ClearTraits = true,
                    Gear = new Fx.StageGear(),
                    Cell = new(3, 1),
                    MentalState = "Berserk",
                },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var subduer = s.Staged!.Pawns["subduer"].Id;
        var target = s.Staged.Pawns["target"].Id;
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedPawnOps.ApplyAsync(s, "subdue", TypedPawnOps.GiveJob(subduer, "AttackMelee", target), cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("subdue-contained", s.TicksLeft, async ct =>
        {
            var row = await TypedPawnOps.PawnAsync(s, "contained", target, ct).ConfigureAwait(false);
            if (row is null || row.Dead)
            {
                throw new AcceptanceException($"the subdue killed the colonist: {row}");
            }

            if (row.Prisoner)
            {
                throw new AcceptanceException($"the subdue made the colonist a prisoner: {row}");
            }

            var aggro = row.MentalState?.IsAggro == true;
            s.RecordWire("contained", row);
            return (string.Create(CultureInfo.InvariantCulture, $"{row.Downed} {aggro} {row.Pawn.Position}"), row.Downed || !aggro);
        }, cancellationToken).ConfigureAwait(false);
    }
}
