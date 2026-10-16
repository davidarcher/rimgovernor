using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// campaign/recovery: after half a day of settled play a raid is staged (test/defense_setup, an edge walk-in at the storyteller's floor points, the
/// sapper-capable group maker); after a day of play the colony must have no live hostile standing, every initial
/// colonist alive, shelter and food intact, and goal progress still advancing. Each fixture op is an injection, not
/// assistance: the harness never touches the colony's own work (Go <c>campaign/recovery.go</c>).
/// </summary>
internal static class CampaignRecoveryCase
{
    internal static AcceptanceCase Case()
    {
        var window = CampaignRun.FootholdWindow();
        var settle = window / 6;
        var raidWindow = window / 3;
        return CampaignRun.CampaignCase(
            "campaign/recovery",
            "Unassisted campaign: a raid recovered on the player control path.",
            "half a day of settling and a day around the raid at Ultrafast with three reloads",
            TimeSpan.FromMinutes(60),
            async (s, cancellationToken) =>
            {
                var c = CampaignRun.Create(s);
                var h = s.Harness;
                var initial = await CampaignRun.InitialColonistsAsync(h, c.Report, cancellationToken).ConfigureAwait(false);
                c.SetupDone();

                // Settle: the foothold goals are running before anything breaks.
                var settled = await c.PlayAsync(
                    "settle",
                    new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(10),
                        Window = settle,
                        PollTicks = 2500,
                        Concern = PolicyFunctions.FoodSupplyNeed.Value,
                        Extra = CampaignRun.CampaignConcerns,
                        FailFast = new FailFast { Disabled = true },
                    },
                    cancellationToken).ConfigureAwait(false);
                if (settled.AssertPlaying(settle) is { } notPlaying)
                {
                    throw notPlaying;
                }

                c.Milestone("settled", settled.LastTick());

                // The raid: staged on the paused game, then played through.
                var raid = await c.FixtureAsync(
                    "raid",
                    "test/defense_setup",
                    new JsonObject { ["op"] = "raid", ["strategy"] = "ImmediateAttack", ["arrival"] = "EdgeWalkIn" },
                    cancellationToken).ConfigureAwait(false);
                c.Inject(
                    "raid",
                    new JsonObject
                    {
                        ["points"] = raid["points"]?.DeepClone(),
                        ["added"] = raid["added"]?.DeepClone(),
                        ["group_maker_seed"] = raid["groupMakerSeed"]?.DeepClone(),
                        ["applied"] = raid["applied"]?.DeepClone(),
                        ["tick"] = settled.LastTick(),
                    });
                c.Milestone("raid_staged", settled.LastTick());
                var fought = await c.PlayAsync(
                    "raid",
                    new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(15),
                        Window = raidWindow,
                        PollTicks = 2500,
                        Concern = PolicyFunctions.FoodSupplyNeed.Value,
                        Extra = CampaignRun.CampaignConcerns,
                        FailFast = new FailFast { Disabled = true },
                    },
                    cancellationToken).ConfigureAwait(false);
                c.Milestone("raid_window_end", fought.LastTick());
                var inspect = await c.FixtureAsync("inspect-after-raid", "test/defense_setup", new JsonObject { ["op"] = "inspect" }, cancellationToken).ConfigureAwait(false);
                c.Report["after_raid"] = new JsonObject { ["hostiles"] = inspect["hostiles"]?.DeepClone(), ["colonists"] = inspect["colonists"]?.DeepClone() };
                var failures = new List<Exception?>();
                var standing = LiveHostiles(inspect);
                if (standing.Count > 0)
                {
                    failures.Add(new AcceptanceException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"raid: {standing.Count} hostile(s) still standing after {raidWindow} ticks: [{string.Join(' ', standing)}]")));
                }

                failures.Add(fought.AssertPlaying(raidWindow));
                failures.Add(fought.AssertProgress());
                failures.Add(await SustainedFoodCase.CaptureAsync(() => c.AuditColonyAsync(h, "end_state", initial, cancellationToken)).ConfigureAwait(false));
                failures.Add(c.AssertUnassisted());
                if (SustainedFoodCase.Join(failures) is { } error)
                {
                    throw error;
                }
            });
    }

    /// <summary>Lists the hostiles an inspect reply shows neither dead nor downed.</summary>
    private static List<string> LiveHostiles(JsonObject inspect)
    {
        var live = new List<string>();
        foreach (var row in inspect["hostiles"].AsSlice())
        {
            var dead = row.Member("dead").AsBool() == true;
            var downed = row.Member("downed").AsBool() == true;
            if (!dead && !downed)
            {
                live.Add(row.Member("id").AsString());
            }
        }

        return live;
    }
}
