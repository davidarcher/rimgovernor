using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using Fx = RimGovernor.Fixtures.Contracts;
using IdeoligionDesign = RimGovernor.Contracts.Wire.Common.IdeoligionDesign;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>ideology/legal-reform</c> (#1662): a pinned eligible fluid ideoligion changes a plain precept through Actions/Apply,
/// consumes development points once and increments its reform count once; a stale or invalid design is refused without
/// mutation and an uncertain-result resend with a new key never reforms twice.
/// </summary>
internal static class IdeologyReform
{
    internal const string PrepareTool = "test/ideoligion_design_prepare";
    private const string InspectTool = "test/ideoligion_design_inspect";

    internal static readonly FixtureOp<NoArgs, Fx.IdeoligionDesignPrepared> Prepare = new(
        PrepareTool,
        _ => [],
        ServedOps.Succeeded<Fx.IdeoligionDesignPrepared>(PrepareTool, r => r.Success && r.IdeoId.Length > 0));

    private static readonly FixtureOp<NoArgs, Fx.IdeoligionDesignRead> Inspect = ServedOps.Read<Fx.IdeoligionDesignRead>(InspectTool);

    internal static TypedCase Case() => new()
    {
        Name = "ideology/legal-reform",
        Scope = "#1662: a pinned eligible fluid ideoligion changes a plain precept through Actions/Apply, consumes development points once "
            + "and increments its reform count once. A stale or invalid design is refused without mutation; an uncertain-result resend "
            + "with a new key observes the applied target and never reforms twice; a spent eligibility is refused.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart(),
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        QuietWorld = true,
        Ops = [PrepareTool, InspectTool],
        Run = RunAsync,
    };

    // Native sorts selections; the requested set is canonicalized the same way before it is compared.
    private static IdeoligionDesign Sorted(IdeoligionDesign design) => design with
    {
        Memes = EquatableArray.From(design.Memes.Order(StringComparer.Ordinal)),
        Precepts = EquatableArray.From(design.Precepts.Order(StringComparer.Ordinal)),
    };

    internal static IdeoligionDesign Decode(string label, string text)
    {
        try
        {
            return JsonSerializer.Deserialize(text, WireJsonContext.Default.CommonIdeoligionDesign)
                ?? throw new AcceptanceException($"{label}: the ideoligion design is empty");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{label}: the ideoligion design does not decode: {e.Message}", e);
        }
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare-design", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var expected = Decode("prepare-design", prepared.Expected);
        var design = Decode("prepare-design", prepared.Design);
        var count = prepared.Count;
        var points = (await s.FixtureAsync("before", Inspect, NoArgs.Value, cancellationToken).ConfigureAwait(false)).Points;
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task Unchanged(string label, IdeoligionDesign want, int wantCount, int wantPoints)
        {
            var got = await s.FixtureAsync(label, Inspect, NoArgs.Value, cancellationToken).ConfigureAwait(false);
            s.Record(label, ServedOps.Json(got));
            if (Decode(label, got.Design) != Sorted(want) || got.Count != wantCount || got.Points != wantPoints)
            {
                throw new AcceptanceException($"{label}: unexpected native state {ServedOps.Json(got)}");
            }
        }

        Task<Operations.ActionResult> Send(string key, int wantCount, IdeoligionDesign wantExpected, IdeoligionDesign target) =>
            TradeKit.ApplyAsync(s, key, new Operations.IdeoligionReformIntent
            {
                IdeoId = prepared.IdeoId,
                Expected = wantExpected,
                Design = target,
                ExpectedReformCount = wantCount,
            }, cancellationToken);

        async Task Refused(string key, int wantCount, IdeoligionDesign wantExpected, IdeoligionDesign target, IdeoligionDesign still, int stillCount, int stillPoints)
        {
            var result = await Send(key, wantCount, wantExpected, target).ConfigureAwait(false);
            if (TradeKit.RefusedCode(result) is null)
            {
                throw new AcceptanceException($"{key}: expected a refusal, got {result}");
            }

            await Unchanged(key + "-unchanged", still, stillCount, stillPoints).ConfigureAwait(false);
        }

        var stale = expected with { Precepts = EquatableArray.From(expected.Precepts.Append("UnknownPrecept")) };
        await Refused("stale-reform", count, stale, design, expected, count, points).ConfigureAwait(false);
        await Refused("unknown", count, expected, design with { Precepts = EquatableArray.From(design.Precepts.Append("UnknownPrecept")) }, expected, count, points).ConfigureAwait(false);
        await Refused("duplicate", count, expected, design with { Precepts = EquatableArray.From(design.Precepts.Append(design.Precepts[0])) }, expected, count, points).ConfigureAwait(false);
        await Refused("missing-memes", count, expected, design with { Memes = default }, expected, count, points).ConfigureAwait(false);

        foreach (var key in new[] { "legal-reform", "uncertain-resend-new-key" })
        {
            var result = await Send(key, count, expected, design).ConfigureAwait(false);
            var effect = TradeKit.Applied(key, result).Ideoligion;
            if (effect?.IdeoId != prepared.IdeoId || effect.Design != Sorted(design))
            {
                throw new AcceptanceException($"{key}: expected {prepared.IdeoId} reformed to the target design, got {effect}");
            }

            await Unchanged(key + "-observed", design, count + 1, 0).ConfigureAwait(false);
        }

        await Refused("ineligible-reform", count + 1, design, expected, design, count + 1, 0).ConfigureAwait(false);
    }
}
