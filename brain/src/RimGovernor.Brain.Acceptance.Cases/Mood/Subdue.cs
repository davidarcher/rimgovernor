using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/subdue</c> (Go <c>subdue.go</c>): ordinary melee containment through a GiveJobIntent AttackMelee on
/// Actions/Apply, ending with the target living, downed or recovered and never converted to a prisoner.
/// </summary>
internal static class Subdue
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/subdue",
        Scope = "Ordinary melee containment through a GiveJobIntent AttackMelee on Actions/Apply: draft and order a blunt attack, resend applies again, and the colonist ends living, downed or recovered, without prisoner conversion.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/subdue_prepare", ArgsFrom = StarterSiteHost.ArgsFor(7) },
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var pawnId = prepared["pawn"].AsString();
        var targetId = prepared["target"].AsString();
        if (pawnId.Length == 0 || targetId.Length == 0)
        {
            throw new AcceptanceException($"subdue fixture lacks identities: {prepared.ToJson()}");
        }

        var intent = ApplyIntents.GiveJob(pawnId, "AttackMelee", targetId);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "grant", identity, cancellationToken).ConfigureAwait(false);
        var staged = await h.CallAsync("stage-legal", "test/subdue_stage", new JsonObject { ["pawnId"] = pawnId, ["targetId"] = targetId, ["scenario"] = "legal" }, cancellationToken).ConfigureAwait(false);
        if (!staged["success"].Flag())
        {
            throw new AcceptanceException($"stage legal: {staged.ToJson()}");
        }

        var executed = await ApplyIntents.ApplyOneAsync(h, "apply-legal", identity, "subdue-legal", intent, cancellationToken).ConfigureAwait(false);
        var job = ApplyIntents.AppliedJob("subdue", executed);
        if (!job["issued"].Flag() || job["jobDef"].AsString() != "AttackMelee")
        {
            throw new AcceptanceException($"subdue was not ordered: {executed.ToJson()}");
        }

        var blunt = await h.CallAsync("blunt-preference", "test/subdue_inspect", new JsonObject { ["targetId"] = targetId, ["pawnId"] = pawnId }, cancellationToken).ConfigureAwait(false);
        if (!blunt["blunt"].Flag())
        {
            throw new AcceptanceException($"subdue did not prefer a legal blunt attack: {blunt.ToJson()}");
        }

        var again = await ApplyIntents.ApplyOneAsync(h, "apply-legal-again", identity, "subdue-legal-again", intent, cancellationToken).ConfigureAwait(false);
        if (ApplyIntents.AppliedJob("subdue resend", again)["issued"].Flag())
        {
            throw new AcceptanceException($"subdue resend issued a second job: {again.ToJson()}");
        }

        JsonObject? facts = null;
        _ = await NativeReads.RunUntilAsync(
            s,
            "subdue-contained",
            6000,
            async ct =>
            {
                var got = await h.CallAsync("subdue-inspect", "test/subdue_inspect", new JsonObject { ["targetId"] = targetId, ["pawnId"] = pawnId }, ct).ConfigureAwait(false);
                facts = got;
                if (!got["alive"].Flag())
                {
                    throw new AcceptanceException($"subdue killed the colonist: {got.ToJson()}");
                }

                var downed = got["downed"].Flag();
                var aggro = got["aggro"].Flag();
                return ($"{(downed ? "true" : "false")} {(aggro ? "true" : "false")}", downed || !aggro);
            },
            quiet: true,
            cancellationToken).ConfigureAwait(false);
        if (facts?["prisoner"].AsBool() is not false)
        {
            throw new AcceptanceException($"containment failed: {facts.ToJson()}");
        }

        s.Report["containment"] = facts.DeepClone();
    }
}
