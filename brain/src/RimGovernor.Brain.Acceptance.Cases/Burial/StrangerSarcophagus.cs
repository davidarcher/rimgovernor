using System.Globalization;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>burial/stranger-sarcophagus</c> (#3242): the KnowBuriedInSarcophagus probe, a native haul that gives no memory.
/// test/tomb_stage stays bespoke: the stage primitives cannot spawn a corpse.
/// </summary>
internal static class StrangerSarcophagus
{
    private const string StageTool = "test/tomb_stage";
    private const string ReadTool = "test/tomb_read";

    // Ticks the read waits after the haul, so a memory gained a moment late would show.
    private const int Settle = 600;

    private static readonly FixtureOp<NoArgs, Fx.TombStaged> Staged = new(
        StageTool,
        _ => [],
        (label, reply) => ServedOps.Reply<Fx.TombStaged>(StageTool)(label, reply) is { Success: true, Sarcophagus.Length: > 0, Corpse.Length: > 0 } staged
            ? staged
            : throw new AcceptanceException($"{label}: {StageTool} refused: {reply?.ToJsonString()}"));

    private static readonly FixtureOp<Fx.TombStaged, Fx.TombRead> Read = new(
        ReadTool,
        staged => ServedOps.Args(("sarcophagus", staged.Sarcophagus), ("ids", "," + staged.Corpse)),
        ServedOps.Reply<Fx.TombRead>(ReadTool));

    internal static TypedCase Case() => new()
    {
        Name = "burial/stranger-sarcophagus",
        Scope = "The KnowBuriedInSarcophagus probe (#3242, epic #3217): a colonist natively hauls a stranger corpse into a fresh "
            + "sarcophagus, and no colonist gains KnowBuriedInSarcophagus and the corpse's everBuriedInSarcophagus stays false. "
            + "Building_Sarcophagus.Notify_HauledTo returns unless thisIsFirstBodyEver, which only TryAcceptThing sets and no haul "
            + "path calls. Mood stays live so a memory could land; unassigned colonists haul on their own (#3669). Vanilla "
            + "physics no unit test runs; it is the negative benchmark the mechanic-card gate cites.",
        Ticks = 10000,
        Tier = CaseTier.OnDemand,
        Ops = [StageTool, ReadTool],
        QuietWorld = true,
        Keep = ["Mood"],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = await s.FixtureAsync("tomb-stage", Staged, NoArgs.Value, cancellationToken).ConfigureAwait(false);

        Fx.TombCorpse? Corpse(Fx.TombRead read) => read.Corpses.FirstOrDefault(c => c.Id == staged.Corpse);

        var ticks = await s.RunUntilAsync("tomb-haul", s.TicksLeft - Settle, async ct =>
        {
            var read = await s.FixtureAsync("tomb-read", Read, staged, ct).ConfigureAwait(false);
            var inside = Corpse(read)?.InSarcophagus == true;
            return (Wait.Signature(string.Create(CultureInfo.InvariantCulture, $"inside={inside} holds={read.Holds}")), inside);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("burial_ticks", ticks);

        await s.RunTicksAsync("settle", Settle, cancellationToken).ConfigureAwait(false);
        var settled = await s.FixtureAsync("tomb-read-settled", Read, staged, cancellationToken).ConfigureAwait(false);
        s.Record("settled", ServedOps.Json(settled));
        if (settled.Colonists.Count == 0)
        {
            throw new AcceptanceException("no colonist read");
        }

        foreach (var colonist in settled.Colonists)
        {
            if (!colonist.Mood || colonist.Memories != 0)
            {
                throw new AcceptanceException($"colonist {colonist.Id} has {colonist.Memories} {TombRules.KnowBuriedInSarcophagusThought} memories (mood need {colonist.Mood}), want 0");
            }
        }

        if (Corpse(settled) is not { InSarcophagus: true, EverBuried: false })
        {
            throw new AcceptanceException($"the hauled corpse is not in the sarcophagus with everBuriedInSarcophagus false: {Corpse(settled)}");
        }
    }
}
