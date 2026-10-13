using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/apparel-policy</c> (Go <c>apparel_policy.go</c>): the ApparelPolicyIntent creates, assigns and updates the
/// pawn's own outfit, labelled with its short name (#1302), reapplies as applied, and overrides manual policies and
/// forced/locked apparel under autonomous control. A Go test cannot see the native filter write.
/// </summary>
internal static class ProductionApparelPolicy
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/apparel-policy",
        Scope = "The ApparelPolicyIntent creates, assigns and updates the pawn's own outfit, labelled with its short name (#1302),, reapplies as applied, and overrides manual policies and forced/locked apparel under autonomous control. A Go test cannot see the native filter write.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;

        Task<JsonObject> Fixture(string mode) =>
            h.CallAsync(mode, "test/gear_fixture", new JsonObject { ["mode"] = mode }, cancellationToken);

        var before = await Fixture("policy_setup").ConfigureAwait(false);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "apparel-auto", identity, cancellationToken).ConfigureAwait(false);

        async Task Apply(string label, JsonObject row, double hp, IReadOnlyList<string> defs)
        {
            var intent = new JsonObject
            {
                ["pawnId"] = row["pawn"]?.DeepClone(),
                ["name"] = row["shortName"]?.DeepClone(),
                ["allowedDefs"] = new JsonArray([.. defs.Select(d => (JsonNode?)JsonValue.Create(d))]),
                ["minHitPoints"] = hp,
                ["maxHitPoints"] = 1,
                ["minQuality"] = 1,
                ["maxQuality"] = 6,
            };
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = label, ["apparelPolicy"] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one result: {reply.ToJson()}");
            }

            if (results[0].AsMap()?["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{label}: apparel not applied: {reply.ToJson()}");
            }
        }

        IReadOnlyList<string> defs = ["Apparel_BasicShirt", "Apparel_Pants"];
        double[] hitPoints = [.51, .65, .65];
        for (var i = 0; i < hitPoints.Length; i++)
        {
            var hp = hitPoints[i];
            var row = before;
            if (i > 0)
            {
                row = await Fixture("policy_read").ConfigureAwait(false);
                defs = ["Apparel_Pants"];
            }

            var label = string.Create(CultureInfo.InvariantCulture, $"apparel-write-{i}");
            await Apply(label, row, hp, defs).ConfigureAwait(false);
            var after = await Fixture("policy_read").ConfigureAwait(false);
            var forced = after["forced"] is null ? 0 : after["forced"].AsNumber();
            var locked = after["locked"].AsBool() == true;
            var tainted = after["tainted"].AsBool() == true;
            var clean = after["clean"].AsBool() == true;
            var minHp = after["minHP"] is null ? 0 : after["minHP"].AsNumber();
            var actualDefs = after["defs"].AsSlice();
            if (after["name"].AsString() != before["shortName"].AsString()
                || forced != 0 || locked || tainted || !clean
                || minHp < hp - .00001 || minHp > hp + .00001
                || actualDefs.Count != defs.Count
                || after["minQuality"].AsNumber() != 1 || after["maxQuality"].AsNumber() != 6)
            {
                throw new AcceptanceException($"native filter/assignment mismatch: {after.ToJson()}");
            }

            for (var j = 0; j < defs.Count; j++)
            {
                if (actualDefs[j].AsString() != defs[j])
                {
                    throw new AcceptanceException($"definition mismatch: {after["defs"].ToJson()}");
                }
            }

            // The player's outfit the pawn alone held is reused, relabelled.
            if (!Dyn.DeepEqual(after["policy"], before["policy"]))
            {
                throw new AcceptanceException("update created another policy");
            }

            s.Report[label] = after;
        }

        var edited = await Fixture("policy_edit").ConfigureAwait(false);
        await Apply("apparel-repair-player-edit", edited, .51, ["Apparel_Pants"]).ConfigureAwait(false);
        var final = await Fixture("policy_read").ConfigureAwait(false);
        if (final["tainted"].AsBool() == true)
        {
            throw new AcceptanceException("manual tainted edit not overridden");
        }

        s.Report["autonomous_override"] = final;
    }
}
