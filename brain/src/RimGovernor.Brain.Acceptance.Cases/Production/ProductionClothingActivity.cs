using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/clothing-activity[-unfinished]</c>: a clothing deficit the served brain meets through Hands. The native
/// recipe makes one garment of the planner's chosen stuff with its input conserved, and the participants are released;
/// the unfinished case supplies one native-minimum quantity of one material, interrupts the unfinished garment once
/// and needs it resumed. test/clothing_activity_prepare prepares the wearer, bench and materials (no bill, recipe or
/// assignment) and records the recipe's input and output, which the primitives cannot express.
/// </summary>
internal static class ProductionClothingActivity
{
    private const string Prepare = "test/clothing_activity_prepare";
    private const string ReadTool = "test/clothing_activity_read";

    private static readonly FixtureOp<bool, Fx.FixtureDone> Prepared = new(Prepare, interrupt => ServedOps.Args(("interrupt", interrupt)), ServedOps.Done(Prepare));

    private static readonly FixtureOp<NoArgs, Fx.ClothingActivityRead> Read = ServedOps.Read<Fx.ClothingActivityRead>(ReadTool);

    internal static TypedCase Case(bool interrupted = false) => new()
    {
        Name = "production/clothing-activity" + (interrupted ? "-unfinished" : ""),
        Scope = "A prepared clothing deficit is met by the served brain through Hands: the native recipe makes one garment of "
            + "the selected stuff with its input conserved, and the participants are released"
            + (interrupted ? "; the unfinished garment is interrupted once and resumed" : "")
            + ". A unit test cannot prove native recipe conversion or unfinished-item conservation.",
        Ticks = 12000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, ReadTool],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "clothing" },
        Run = (s, ct) => RunAsync(s, interrupted, ct),
    };

    private static async Task RunAsync(TypedSession s, bool interrupted, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("clothing-prepare", Prepared, interrupted, cancellationToken).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("clothing", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("clothing-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("clothing", ServedOps.Json(read));
            if (read.Produced && !read.Conserved)
            {
                throw new AcceptanceException($"recipe input and product are not conserved: {ServedOps.Json(read)}");
            }

            var done = read.Produced && read.Conserved && read.Released > 0 && (!interrupted || read.Interrupted && read.Resumed > 0);
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick}"), done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
