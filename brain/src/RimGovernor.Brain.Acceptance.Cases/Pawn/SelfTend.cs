using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class PawnSelfTend
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/self-tend",
        Scope = "Explicit native self-tend permission is readable and remains enabled after normal assignment review. Native setting/readback and live review preservation require a game; assignment admission is also unit tested. This does not claim autonomous treatment or automatic permission selection.",
        Start = new FixtureStart { Op = "test/lone_self_tend_off", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawn = PawnSupport.Fixture(s, "pawn", "pawn");
        s.Report["pawn"] = pawn;
        var before = await PawnSupport.ReadPawnAsync(s.Harness, s, "self-tend-before", pawn, true, cancellationToken).ConfigureAwait(false);
        if (before["settings"].Member("selfTend").AsBool() == true)
            throw new AcceptanceException("Prepared pawn did not start with self-tend disabled.");
        var write = await ApplyIntents.ApplyOneAsync(s.Harness, "enable-self-tend", s.Identity, "enable-self-tend",
            new() { ["pawnSettings"] = JsonNode.Parse(WireJson.Serialize(new Op.PawnSettingsIntent
            { PawnId = pawn, Setting = new Op.PawnSettingsIntentSetting.SelfTendCase(true) })) }, cancellationToken).ConfigureAwait(false);
        _ = ApplyIntents.AppliedJob("enable-self-tend", write);
        var enabled = await PawnSupport.ReadPawnAsync(s.Harness, s, "self-tend-enabled", pawn, true, cancellationToken).ConfigureAwait(false);
        if (enabled["settings"].Member("selfTend").AsBool() != true)
            throw new AcceptanceException("Explicit self-tend setting did not read back enabled.");
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var review = await service.WaitReviewAsync(new Wait { Ceiling = TimeSpan.FromSeconds(60), Stall = TimeSpan.FromSeconds(45) },
                r => r["lastReviewTick"].AsUInt64() is > 0, cancellationToken).ConfigureAwait(false);
            s.Report["review"] = review.DeepClone();
        }
        finally { _ = await service.StopAsync().ConfigureAwait(false); }
        _ = s.Reattach();
        var final = await PawnSupport.ReadPawnAsync(s.Harness, s, "self-tend-after-review", pawn, true, cancellationToken).ConfigureAwait(false);
        var settings = final["settings"].AsMap() ?? [];
        s.Report["self_tend"] = settings["selfTend"].AsBool() == true;
        s.Report["tend_quality"] = settings["policyInputs"].Member("medicalTendQuality")?.DeepClone();
        if (settings["selfTend"].AsBool() != true)
            throw new AcceptanceException("Normal assignment review revoked explicit self-tend permission.");
        if (s.Report["tend_quality"] is null)
            throw new AcceptanceException("Doctor-capable pawn read carried no medical tend quality.");
    }
}
