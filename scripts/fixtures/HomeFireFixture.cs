#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Native
{
    // control/home-fire-release (#3672): two capable colonists on committed orders, one holding and one sleeping, and a
    // fire set on staged kindling inside the home area.
    public sealed partial class PawnControlFixture
    {
        [Tool("test/home_fire_install", Description = "UNSAFE FOR MODEL EXECUTION. Install a long hold for the fighter and a sleep in the bed for the sleeper, then set the kindling alight. Test builds only.")]
        public async Task<object> HomeFireInstall(INativeContext ctx, CancellationToken ct,
            [ToolParameter(Description = "The holding colonist's load id.")] string fighter = "",
            [ToolParameter(Description = "The sleeping colonist's load id.")] string sleeper = "",
            [ToolParameter(Description = "The sleeper's bed's load id.")] string bed = "",
            [ToolParameter(Description = "The kindling's load id.")] string kindling = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            var holder = FixtureKit.Spawned<Pawn>(map, fighter);
            var resting = FixtureKit.Spawned<Pawn>(map, sleeper);
            var sleepBed = FixtureKit.Spawned<Building_Bed>(map, bed);
            var fuel = FixtureKit.Spawned<Thing>(map, kindling);
            foreach (var p in new[] { holder, resting })
                if (p.WorkTagIsDisabled(WorkTags.Firefighting) || p.workSettings?.WorkIsActive(WorkTypeDefOf.Firefighter) != true)
                    throw new InvalidOperationException($"Staged colonist {p.LabelShort} cannot fight fires.");
            var until = Find.TickManager.TicksGame + 60000;
            Apply(
                new PawnActivityOrder(holder.GetUniqueLoadID(), "fire-holder", "fire-method", 0, 1, false, new[] { Hold(until) }),
                new PawnActivityOrder(resting.GetUniqueLoadID(), "fire-sleeper", "fire-method", 0, 1, false,
                    new[] { new PawnActivityStep(PawnActivityKind.Sleep, sleepBed.GetUniqueLoadID(), "", "", 0, until) }));
            if (!map.areaManager.Home[fuel.Position] || !FireUtility.TryStartFireIn(fuel.Position, map, 0.5f, null))
                throw new InvalidOperationException("The kindling did not catch fire inside the home area.");
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);
    }
}
