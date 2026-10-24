using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/service-investment</c>: a cold occupied room with a partly funded heater frame, under the served brain.
/// The native game finishes the heater frame, powers it and warms the room, while an unrelated lower-tier wall frame
/// competing for the same five steel stays unbuilt. test/service_shelter_prepare builds the room, frames, steel and
/// generator (no task or assignment) and records the frame work, which the primitives cannot express; the governor's
/// layout plan is staged to describe that standing room as a planned barracks.
/// </summary>
internal static class ShelterServiceInvestment
{
    private const string Prepare = "test/service_shelter_prepare";
    private const string ReadTool = "test/service_shelter_read";

    private static readonly FixtureOp<NoArgs, Fx.ServiceShelterPrepared> Prepared = new(
        Prepare,
        _ => [],
        (label, reply) => ServedOps.Reply<Fx.ServiceShelterPrepared>(Prepare)(label, reply) is { Success: true } prepared
            ? prepared
            : throw new AcceptanceException($"{label}: {Prepare} refused: {reply?.ToJsonString()}"));

    private static readonly FixtureOp<NoArgs, Fx.ServiceShelterRead> Read = ServedOps.Read<Fx.ServiceShelterRead>(ReadTool);

    internal static TypedCase Case() => new()
    {
        Name = "shelter/service-investment",
        Scope = "Under the served brain the native game finishes a cold occupied room's partly funded heater frame, powers it "
            + "and warms the room, leaving an unrelated lower-tier wall frame that competes for the same five steel unbuilt. A "
            + "pure test cannot prove native frame work, power or warming.",
        Ticks = 12000,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec { World = new Fx.StageWorld { OutdoorTemperature = -10 } },
        Ops = [Prepare, ReadTool],
        Crew = new Crew { Size = 2 },
        Keep = ["Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "service-investment" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var room = await s.FixtureAsync("service-prepare", Prepared, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            Cold = true,
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Barracks,
                    Region = RegionRules.RectRegion(new(room.X, room.Z, 3, 3)),
                    Door = new(room.DoorX, room.DoorZ),
                    DoorSide = new DoorSide.Doored(Rotation.East),
                },
            ],
        }, cancellationToken).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("service-investment", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("service-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("investment", ServedOps.Json(read));
            if (!read.SparePresent || read.SpareRemainingWork < 99)
            {
                throw new AcceptanceException($"the unrelated wall frame was built or cancelled: {ServedOps.Json(read)}");
            }

            var done = read.Worked && read.Completed && read.Powered && read.Temperature > 0;
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick}"), done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
