using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using IdeoligionDesign = RimGovernor.Contracts.Wire.Common.IdeoligionDesign;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>ideology/legal-reform</c> (Go <c>reform.go</c>): a pinned eligible fluid ideoligion changes a plain precept through
/// Actions/Apply, consumes development points once and increments its reform count once; a stale or invalid design is
/// refused without mutation and an uncertain-result resend with a new key never reforms twice (#1662).
/// </summary>
internal static class IdeologyReform
{
    private const string PrepareOp = "test/ideoligion_design_prepare";
    private const string InspectOp = "test/ideoligion_design_inspect";

    internal static AcceptanceCase Case() => new()
    {
        Name = "ideology/legal-reform",
        Scope = "#1662: a pinned eligible fluid ideoligion changes a plain precept through Actions/Apply, consumes development points once "
            + "and increments its reform count once. A stale design is refused without mutation; an uncertain-result resend with a new key "
            + "observes the applied target and never reforms twice. Snapshots cannot prove the native legality/apply/progression contract.",
        Start = new FixtureStart { Op = PrepareOp, On = new LabStart() },
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareOp, InspectOp],
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // Native sorts selections; the requested set is canonicalized the same way before it is compared.
    private static IdeoligionDesign Sorted(IdeoligionDesign design) => design with
    {
        Memes = EquatableArray.From(design.Memes.Order(StringComparer.Ordinal)),
        Precepts = EquatableArray.From(design.Precepts.Order(StringComparer.Ordinal)),
    };

    private static IdeoligionDesign ParseDesign(JsonNode? text)
    {
        try
        {
            return JsonSerializer.Deserialize(text.AsString(), WireJsonContext.Default.CommonIdeoligionDesign)
                ?? throw new AcceptanceException("the ideoligion design is empty");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"the ideoligion design does not decode: {e.Message}", e);
        }
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var prepared = await h.CallAsync("prepare-design", PrepareOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"prepare: {prepared.ToJson()}");
        }

        var expected = ParseDesign(prepared["expected"]);
        var design = ParseDesign(prepared["design"]);
        var count = (int)prepared["count"].AsNumber();

        async Task Inspect(string label, IdeoligionDesign want, int wantCount, int wantPoints)
        {
            var got = await h.CallAsync(label, InspectOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
            var actual = ParseDesign(got["design"]);
            if (actual != Sorted(want) || (int)got["count"].AsNumber() != wantCount || (int)got["points"].AsNumber() != wantPoints)
            {
                throw new AcceptanceException($"{label}: unexpected native state {got.ToJson()}");
            }

            s.Report[label] = got.DeepClone();
        }

        var before = await h.CallAsync("before", InspectOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
        var points = (int)before["points"].AsNumber();

        async Task<JsonObject> Send(string key, int wantCount, IdeoligionDesign wantExpected, IdeoligionDesign target)
        {
            var body = new JsonObject
            {
                ["identity"] = s.Identity.DeepClone(),
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["key"] = key,
                    ["ideoligionReform"] = new JsonObject
                    {
                        ["ideoId"] = prepared["ideoId"]?.DeepClone(),
                        ["expected"] = JsonSerializer.SerializeToNode(wantExpected, WireJsonContext.Default.CommonIdeoligionDesign),
                        ["design"] = JsonSerializer.SerializeToNode(target, WireJsonContext.Default.CommonIdeoligionDesign),
                        ["expectedReformCount"] = wantCount,
                    },
                }),
            };
            return await h.WireAsync(key, "operations_apply", body, cancellationToken).ConfigureAwait(false);
        }

        static JsonObject OnlyRow(JsonObject reply, string what)
        {
            var rows = reply["results"].AsSlice();
            return rows.Count != 1 ? throw new AcceptanceException($"{what}: {reply.ToJson()}") : rows[0].AsMap() ?? [];
        }

        var stale = expected with { Precepts = EquatableArray.From(expected.Precepts.Append("UnknownPrecept")) };
        var reply = await Send("stale-reform", count, stale, design).ConfigureAwait(false);
        var row = OnlyRow(reply, "stale reply");
        if (!row.ContainsKey("refused"))
        {
            throw new AcceptanceException($"stale request applied: {reply.ToJson()}");
        }

        await Inspect("stale-unchanged", expected, count, points).ConfigureAwait(false);
        foreach (var bad in new[] { "unknown", "duplicate", "missing-memes" })
        {
            var invalid = bad switch
            {
                "unknown" => design with { Precepts = EquatableArray.From(design.Precepts.Append("UnknownPrecept")) },
                "duplicate" => design with { Precepts = EquatableArray.From(design.Precepts.Append(design.Precepts[0])) },
                _ => design with { Memes = default },
            };
            reply = await Send(bad, count, expected, invalid).ConfigureAwait(false);
            var result = OnlyRow(reply, "invalid reply");
            if (!result.ContainsKey("refused"))
            {
                throw new AcceptanceException($"invalid design applied: {reply.ToJson()}");
            }

            await Inspect(bad + "-unchanged", expected, count, points).ConfigureAwait(false);
        }

        foreach (var key in new[] { "legal-reform", "uncertain-resend-new-key" })
        {
            reply = await Send(key, count, expected, design).ConfigureAwait(false);
            row = OnlyRow(reply, "apply");
            if (!row.ContainsKey("applied"))
            {
                throw new AcceptanceException($"reform refused: {reply.ToJson()}");
            }

            await Inspect(key + "-observed", design, count + 1, 0).ConfigureAwait(false);
        }

        reply = await Send("ineligible-reform", count + 1, design, expected).ConfigureAwait(false);
        row = OnlyRow(reply, "ineligible reply");
        if (!row.ContainsKey("refused"))
        {
            throw new AcceptanceException($"spent eligibility applied: {reply.ToJson()}");
        }

        await Inspect("ineligible-unchanged", design, count + 1, 0).ConfigureAwait(false);
    }
}
