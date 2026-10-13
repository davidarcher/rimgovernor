using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/ancient-arrest</c> and <c>mood/arrest</c> (Go <c>arrest.go</c>): vanilla Arrest custody through the Arrest
/// GiveJobIntent on Actions/Apply, refusals first, then the legal arrest through to the exact prisoner bed.
/// </summary>
internal static class Arrest
{
    internal static IEnumerable<AcceptanceCase> All()
    {
        yield return new AcceptanceCase
        {
            Name = "mood/ancient-arrest",
            Scope = "The Arrest GiveJobIntent delivers a standing neutral Faction.OfAncients pawn to its exact prisoner bed under owned draft; ordinary colonists still require a legal mental state.",
            Start = Starter(),
            Keep = ["Rest"],
            Budget = TimeSpan.FromMinutes(2),
            Crew = new Crew { Size = 3 },
            Run = (s, ct) => RunTargetAsync(s, "ancient", ct),
        };
        yield return new AcceptanceCase
        {
            Name = "mood/arrest",
            Scope = "Vanilla Arrest custody through the Arrest GiveJobIntent on Actions/Apply: refuse normal targets, unarmed arresters, hostile Berserk and non-prisoner beds; require owned draft; a resend applies again; a living sad-wander target ends in the exact prisoner bed with its mental state ended.",
            Start = Starter(),
            Keep = ["Rest"],
            Budget = TimeSpan.FromMinutes(2),
            Crew = new Crew { Size = 3 },
            Run = (s, ct) => RunTargetAsync(s, "legal", ct),
        };
    }

    private static FixtureStart Starter() => new() { On = new LabStart(), Op = "test/arrest_prepare", ArgsFrom = StarterSiteHost.ArgsFor(7) };

    private sealed record Scenario(string Name, string Fixture, string Reason, bool Draft);

    private static async Task RunTargetAsync(ISession s, string legalFixture, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var pawnId = prepared["pawn"].AsString();
        var targetId = prepared["target"].AsString();
        var bedId = prepared["bed"].AsString();
        var ordinaryBedId = prepared["ordinaryBed"].AsString();
        if (pawnId.Length == 0 || targetId.Length == 0 || bedId.Length == 0 || ordinaryBedId.Length == 0)
        {
            throw new AcceptanceException($"arrest fixture lacks identities: {prepared.ToJson()}");
        }

        async Task<JsonObject> ReadAsync(string label)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_pawns",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["filter"] = new JsonObject { ["ids"] = NativeReads.Strings(pawnId) },
                },
                cancellationToken).ConfigureAwait(false);
            return Draft.PawnRow(reply, identity, pawnId);
        }

        Scenario[] scenarios =
        [
            new("normal", "normal", "not living in a mental state", true),
            new("unarmed", "unarmed", "unarmed or incapable of violence", true),
            new("berserk", "berserk", "native arrest eligibility", true),
            new("ordinary-bed", "legal", "prisoner bed", true),
            new("unowned", "legal", "eligible drafted pawn", false),
            new("legal", legalFixture, string.Empty, true),
        ];
        foreach (var scenario in scenarios)
        {
            var staged = await h.CallAsync(
                "stage-" + scenario.Name,
                "test/arrest_stage",
                new JsonObject { ["pawnId"] = pawnId, ["targetId"] = targetId, ["scenario"] = scenario.Fixture },
                cancellationToken).ConfigureAwait(false);
            if (!staged["success"].Flag())
            {
                throw new AcceptanceException($"stage {scenario.Name}: {staged.ToJson()}");
            }

            _ = await Authority.GrantAutoAsync(h.WireFunc(), "grant-" + scenario.Name, identity, cancellationToken).ConfigureAwait(false);
            var row = await ReadAsync("before-" + scenario.Name).ConfigureAwait(false);
            if (scenario.Draft)
            {
                _ = await ApplyIntents.ApplyDraftAsync(h, "draft-" + scenario.Name, identity, "draft-" + scenario.Name, pawnId, true, cancellationToken).ConfigureAwait(false);
                row = await ReadAsync("drafted-" + scenario.Name).ConfigureAwait(false);
            }

            var bed = scenario.Name == "ordinary-bed" ? ordinaryBedId : bedId;
            var intent = ApplyIntents.GiveJob(pawnId, "Arrest", targetId, bed);
            var executed = await ApplyIntents.ApplyOneAsync(h, "apply-" + scenario.Name, identity, "arrest-" + scenario.Name, intent, cancellationToken).ConfigureAwait(false);
            if (scenario.Reason.Length > 0)
            {
                ApplyIntents.Refused(scenario.Name, executed, scenario.Reason);
                s.Report["refused_" + scenario.Name] = executed.DeepClone();
                var after = await ReadAsync("after-refusal-" + scenario.Name).ConfigureAwait(false);
                try
                {
                    Draft.SameControl(row, after);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"refusal mutated arrester: {e.Message}");
                }

                continue;
            }

            var job = ApplyIntents.AppliedJob("arrest", executed);
            if (!job["issued"].Flag() || job["jobDef"].AsString() != "Arrest")
            {
                throw new AcceptanceException($"arrest was not issued: {executed.ToJson()}");
            }

            // A resend while the arrest runs applies again without a second order.
            var again = await ApplyIntents.ApplyOneAsync(h, "apply-legal-again", identity, "arrest-legal-again", intent, cancellationToken).ConfigureAwait(false);
            if (ApplyIntents.AppliedJob("arrest resend", again)["issued"].Flag())
            {
                throw new AcceptanceException($"arrest resend issued a second job: {again.ToJson()}");
            }
        }

        JsonObject? inspected = null;
        _ = await NativeReads.RunUntilAsync(
            s,
            "arrest-custody",
            6000,
            async ct =>
            {
                var got = await h.CallAsync("custody-inspect", "test/arrest_inspect", new JsonObject { ["targetId"] = targetId }, ct).ConfigureAwait(false);
                inspected = got;
                if (!got["alive"].Flag() || got["deadColonists"].AsNumber() != 0)
                {
                    throw new AcceptanceException($"arrest cost a life: {got.ToJson()}");
                }

                var mental = got["mental"].Flag();
                var prisoner = got["prisoner"].Flag();
                return ($"{(mental ? "true" : "false")} {(prisoner ? "true" : "false")} {got["bed"].ToText()}", !mental && prisoner && got["bed"].AsString() == bedId);
            },
            quiet: true,
            cancellationToken).ConfigureAwait(false);
        s.Report["custody"] = inspected?.DeepClone();
    }
}
