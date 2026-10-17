using System.Globalization;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>burial/grave-over-morgue</c> (#2196): a morgue zone at policy.MorguePriority holding a colonist and a stranger
/// corpse beside a free grave. test/burial_stage stays bespoke: the stage primitives cannot spawn corpses.
/// </summary>
internal static class GraveOverMorgue
{
    private const string StageTool = "test/burial_stage";
    private const string ReadTool = "test/burial_read";

    // Ticks the stranger is watched after the colonist's burial: the hauler had every chance at it too.
    private const int Settle = 600;

    private static readonly FixtureOp<NoArgs, Fx.BurialStaged> Staged = new(
        StageTool,
        _ => ServedOps.Args(("priority", DomainWire.Name(PolicyFunctions.MorguePriority))),
        (label, reply) => ServedOps.Reply<Fx.BurialStaged>(StageTool)(label, reply) is { Success: true, ColonistCorpse.Length: > 0, StrangerCorpse.Length: > 0, Grave.Length: > 0 } staged
            ? staged
            : throw new AcceptanceException($"{label}: {StageTool} refused: {reply?.ToJsonString()}"));

    private static readonly FixtureOp<string, Fx.BurialRead> Read = new(ReadTool, ids => ServedOps.Args(("ids", ids)), ServedOps.Reply<Fx.BurialRead>(ReadTool));

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> VanillaColonists = ServedOps.Inject(Needs.VanillaColonistsTool);

    internal static TypedCase Case() => new()
    {
        Name = "burial/grave-over-morgue",
        Scope = "Grave-versus-zone ranking (#2196): vanilla hauling carries a colonist corpse from a morgue zone at "
            + "policy.MorguePriority into a free grave, whose storage priority outranks the morgue, and leaves the stranger, "
            + "whom the grave refuses, in the morgue. Vanilla stockpile ranking and an idle colonist's own haul "
            + "(test/vanilla_colonists) are native physics no unit test runs.",
        Ticks = 10000,
        Tier = CaseTier.OnDemand,
        Ops = [StageTool, ReadTool, Needs.VanillaColonistsTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var staged = await s.FixtureAsync("burial-stage", Staged, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("vanilla-colonists", VanillaColonists, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var ids = staged.ColonistCorpse + "," + staged.StrangerCorpse;

        async Task<(bool ColonistInGrave, bool StrangerInMorgue)> ReadAsync(string label, CancellationToken ct)
        {
            var read = await s.FixtureAsync(label, Read, ids, ct).ConfigureAwait(false);
            var colonist = read.Things.FirstOrDefault(t => t.Id == staged.ColonistCorpse);
            var stranger = read.Things.FirstOrDefault(t => t.Id == staged.StrangerCorpse);
            return stranger is { InGrave: true }
                ? throw new AcceptanceException($"{label}: the stranger corpse was interred in a colonist grave")
                : (colonist?.InGrave == true, stranger?.Zone == true);
        }

        var ticks = await s.RunUntilAsync("grave-haul", s.TicksLeft - Settle, async ct =>
        {
            var (inGrave, inMorgue) = await ReadAsync("burial-read", ct).ConfigureAwait(false);
            return (Wait.Signature(string.Create(CultureInfo.InvariantCulture, $"grave={inGrave} morgue={inMorgue}")), inGrave);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("burial_ticks", ticks);

        await s.RunTicksAsync("settle", Settle, cancellationToken).ConfigureAwait(false);
        var (_, strangerInMorgue) = await ReadAsync("burial-read-settled", cancellationToken).ConfigureAwait(false);
        if (!strangerInMorgue)
        {
            throw new AcceptanceException("the stranger corpse left the morgue zone");
        }
    }
}
