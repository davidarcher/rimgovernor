using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Test-build-only staging for schedule/* (#1318): the adaptive timetable
    // planner reads rest and psylink; nothing in production mutates either.
    public sealed class ScheduleFixture
    {
        [Tool("test/schedule_setup", Description = "Set rest on the first free colonist and optionally give them a psylink; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Rest level 0-1 for the target pawn.")] double rest,
            [ToolParameter(Description = "Grant psylink level 1 (Royalty only).", DefaultValue = false)] bool psylink = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && p.needs?.rest != null && p.needs.joy != null && p.timetable != null);
                pawn.needs.rest.CurLevelPercentage = (float)Math.Max(0, Math.Min(1, rest));
                if (psylink)
                {
                    if (!ModsConfig.RoyaltyActive)
                        throw new InvalidOperationException("psylink needs Royalty.");
                    if (pawn.GetPsylinkLevel() < 1)
                        pawn.ChangePsylinkLevel(1, false);
                    if (pawn.GetPsylinkLevel() < 1)
                        throw new InvalidOperationException("psylink was not granted.");
                }
                return new { success = true, pawn = pawn.GetUniqueLoadID(), rest = pawn.needs.rest.CurLevelPercentage,
                    psylinkLevel = ModsConfig.RoyaltyActive ? pawn.GetPsylinkLevel() : 0 };
            }, cancellationToken);
        }
    }
}
