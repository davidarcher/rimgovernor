using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/shared-stack</c>: the unclaimed remainder of an owned food stack (#3194) under the served brain. One hungry
/// pawn's Method eats from a four-meal stack and owes later bites of it; a second pawn made hungry while that Method
/// still owns the stack eats the units it does not owe, under its own Method, from the same stack.
/// test/shared_stack_prepare and _hunger stage the stack and the hunger (no task, Method or order) and record the
/// applied bites, which the primitives cannot express.
/// </summary>
internal static class NeedsSharedStack
{
    private const string Prepare = "test/shared_stack_prepare";
    private const string HungerTool = "test/shared_stack_hunger";
    private const string ReadTool = "test/shared_stack_read";

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Prepared = ServedOps.Inject(Prepare);
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Hunger = ServedOps.Inject(HungerTool);
    private static readonly FixtureOp<NoArgs, Fx.SharedStackRead> Read = ServedOps.Read<Fx.SharedStackRead>(ReadTool);

    internal static TypedCase Case() => new()
    {
        Name = "needs/shared-stack",
        Scope = "Under the served brain one pawn's Method eats from a four-meal stack and owes later bites of it; a second pawn "
            + "made hungry while that Method still owns the stack eats its unclaimed units under its own Method. Pure tests "
            + "prove the remainder arithmetic, not vanilla ingestion from a stack two Methods share.",
        Ticks = 18000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, HungerTool, ReadTool],
        Crew = new Crew { Size = 2 },
        Keep = ["Food"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "shared-stack" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("shared-stack-prepare", Prepared, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var hungry = false;
        _ = await s.ServeUntilAsync("shared-stack", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("shared-stack-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record(hungry ? "second" : "first", ServedOps.Json(read));
            var signature = string.Create(CultureInfo.InvariantCulture, $"{read.Tick} hungry={hungry}");
            if (!hungry)
            {
                if (read.Methods.Count >= 1)
                {
                    _ = await s.FixtureAsync("shared-stack-hunger", Hunger, NoArgs.Value, ct).ConfigureAwait(false);
                    hungry = true;
                }

                return (signature, false);
            }

            var shared = read.Methods.Count >= 2 && read.Eaters >= 2 && read.SharedWhileOwned;
            if (!shared && read.Units == 0)
            {
                throw new AcceptanceException($"the stack was gone before a second Method ate from it: {ServedOps.Json(read)}");
            }

            return (signature, shared);
        }, cancellationToken).ConfigureAwait(false);
    }
}
