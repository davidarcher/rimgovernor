namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/shock-lance</c> (#1038, #3424): GiveJobIntent UseItem on Actions/Apply of a worn psychic shock lance. On
/// lab-open cut to one club raider, colonist 0 wears a shock lance; one UseItem of the lance on the raider applies, the
/// colonist takes UseVerbOnThingStatic on the raider, and within <see cref="DownTicks"/> the raider is downed by PsychicShock and alive.
/// </summary>
internal static class CombatLabShockLance
{
    private const string ShockLance = "Apparel_PsychicShockLance";

    /// <summary>The lance's cast and the shock's onset.</summary>
    private const int DownTicks = 1200 + CombatLabKit.ProbeTicks;

    // One raider; colonist 0 wears the lance.
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, _, _) =>
    {
        foreach (var extra in f.Pawns.Where(p => p.Side == CombatFixtures.Hostile).Skip(1).ToList())
        {
            _ = f.Pawns.Remove(extra);
        }

        f.Pawns.First(p => p.Side == CombatFixtures.Colonist && p.Index == 0).Apparel = ShockLance;
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/shock-lance",
        Scope = "GiveJobIntent UseItem on Actions/Apply (#1038, #3424), a native op contract and vanilla hediff physics no snapshot can prove: on lab-open cut to one club raider, "
            + "colonist 0 wears a psychic shock lance; one UseItem of the worn lance on the raider applies, the colonist takes UseVerbOnThingStatic on the raider, "
            + "and within the budget the raider is downed with PsychicShock and alive. Which target and wearer is policy.",
        Ticks = DownTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var user = lab.Colonists()[0];
        var raider = lab.Hostiles().Single();
        var wearer = await CombatLabKit.PawnAsync(s, "wearer", user, cancellationToken).ConfigureAwait(false);
        // The staging dresses the wearer in the lance alone: its one worn apparel.
        var lance = wearer.Equipment?.Apparel is [{ } only] ? only.Thing.Id
            : throw new AcceptanceException($"colonist 0: want one worn apparel ({ShockLance}), got {wearer.Equipment?.Apparel.Count}");
        s.Record("lance", lance);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var issued = (await TypedPawns.AppliedAsync(s, "use-lance", TypedPawnOps.GiveJob(user, "UseItem", lance, raider), cancellationToken).ConfigureAwait(false))?.Job;
        if (issued is not { Issued: true } || issued.PawnId != user)
        {
            throw new AcceptanceException($"use_item not issued by colonist 0: {issued}");
        }

        var after = await CombatLabKit.PawnAsync(s, "after-use", user, cancellationToken).ConfigureAwait(false);
        if (CombatLabKit.Job(after) != "UseVerbOnThingStatic" || CombatLabKit.JobThing(after) != raider)
        {
            throw new AcceptanceException($"colonist 0 after use_item: want UseVerbOnThingStatic on the raider, got {CombatLabKit.Job(after)} on {CombatLabKit.JobThing(after)}");
        }

        _ = await s.RunUntilAsync("down", DownTicks, async ct =>
        {
            var p = await CombatLabKit.PawnAsync(s, "raider", raider, ct).ConfigureAwait(false);
            if (p.Dead)
            {
                throw new AcceptanceException("raider died; the lance must down it alive");
            }

            var hediffs = p.Health?.Hediffs.Select(h => h.DefName).ToList() ?? [];
            if (p.Downed && !hediffs.Contains("PsychicShock"))
            {
                throw new AcceptanceException($"raider down without PsychicShock: [{string.Join(' ', hediffs)}]");
            }

            return (Wait.Signature(p.Downed, hediffs.Count), p.Downed);
        }, cancellationToken).ConfigureAwait(false);
    }
}
