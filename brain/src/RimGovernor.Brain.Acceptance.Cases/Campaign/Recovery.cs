using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// campaign/recovery: after half a day of settled play the harness takes every wood log on the map (the wood-floor
/// breach) and resumes; the colony must acknowledge the breach (MaintainResource binds) and restock to the policy floor
/// on its own. A staged raid follows (test/defense_setup, an edge walk-in at the storyteller's floor points, the
/// sapper-capable group maker); after a day of play the colony must have no live hostile standing, every initial
/// colonist alive, shelter and food intact, and goal progress still advancing. Each fixture op is an injection, not
/// assistance: the harness never touches the colony's own work (Go <c>campaign/recovery.go</c>).
/// </summary>
internal static class CampaignRecoveryCase
{
    // Policy wood thresholds the breach is judged against: MaintainResource binds (the wood latch's floor) once stock
    // falls under WoodMin and its deficit is (WoodTarget-stock)/WoodTarget, so a stock back at WoodMin reads as
    // WoodRecoveredDeficit.
    private const long WoodMin = 120;
    private const long WoodTarget = 350;
    private const double WoodRecoveredDeficit = (double)(WoodTarget - WoodMin) / WoodTarget;

    internal static AcceptanceCase Case()
    {
        var window = CampaignRun.FootholdWindow();
        var settle = window / 6;
        var breachWindow = window * 2 / 3;
        var raidWindow = window / 3;
        return CampaignRun.CampaignCase(
            "campaign/recovery",
            "Unassisted campaign: an induced wood-floor breach and then a raid, each recovered on the player control path.",
            "half a day of settling, up to two days restocking wood and a day around the raid at Ultrafast with three reloads",
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
                        Concern = PolicyFunctions.EnsureFoodSupply.Value,
                        Extra = CampaignRun.CampaignConcerns,
                        FailFast = new FailFast { Disabled = true },
                    },
                    cancellationToken).ConfigureAwait(false);
                if (settled.AssertPlaying(settle) is { } notPlaying)
                {
                    throw notPlaying;
                }

                c.Milestone("settled", settled.LastTick());

                // The wood-floor breach: every log on the map is gone.
                var taken = await c.FixtureAsync("take-wood", "test/hut_shell_fixture", new JsonObject { ["action"] = "take" }, cancellationToken).ConfigureAwait(false);
                c.Inject("wood_breach", new JsonObject { ["taken"] = taken["taken"]?.DeepClone(), ["stacks"] = taken["stacks"]?.DeepClone(), ["tick"] = settled.LastTick() });

                ulong breachTick = 0;
                ulong restockTick = 0;
                var breached = false;
                var restocked = await c.PlayAsync(
                    "restock",
                    new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(25),
                        Window = breachWindow,
                        PollTicks = 2500,
                        Concern = PolicyFunctions.MaintainResource.Value,
                        Extra = CampaignRun.CampaignConcerns,
                        FailFast = new FailFast { Disabled = true },
                        Until = sample =>
                        {
                            var tick = sample["tick"].AsUInt64() ?? 0;
                            var bound = sample["concern_bound"].AsBool() == true;
                            var (deficit, known) = WoodDeficit(sample);
                            if (!breached)
                            {
                                if (bound)
                                {
                                    breached = true;
                                    breachTick = tick;
                                }

                                return false;
                            }

                            if (!bound || (known && deficit <= WoodRecoveredDeficit))
                            {
                                restockTick = tick;
                                return true;
                            }

                            return false;
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                if (restocked.Err is not null)
                {
                    throw restocked.Err;
                }

                if (!breached)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"restock: MaintainResource never bound after the breach ({restocked.Timeline.Count} samples)"));
                }

                c.Milestone("wood_breach_acknowledged", breachTick);
                if (restockTick == 0)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"restock: wood not back to the policy floor within {breachWindow} ticks of the breach"));
                }

                c.Milestone("wood_restocked", restockTick);
                var facts = await CampaignColonyFacts.ReadAsync(h, s, "wood-after-restock", cancellationToken).ConfigureAwait(false);
                c.Report["wood_after_restock"] = new JsonObject { ["wood"] = facts.Wood, ["known"] = facts.WoodKnown };
                if (!facts.WoodKnown || facts.Wood < WoodMin)
                {
                    throw new AcceptanceException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"restock: native wood stock {facts.Wood} (known {(facts.WoodKnown ? "true" : "false")}) under the policy floor {WoodMin}"));
                }

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
                        ["tick"] = restockTick,
                    });
                c.Milestone("raid_staged", restockTick);
                var fought = await c.PlayAsync(
                    "raid",
                    new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(15),
                        Window = raidWindow,
                        PollTicks = 2500,
                        Concern = PolicyFunctions.EnsureFoodSupply.Value,
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

    /// <summary>Reads the MaintainResource ranking row's deficit off a sample.</summary>
    private static (double Deficit, bool Known) WoodDeficit(JsonObject sample)
    {
        if (sample["development"] is not JsonObject development || development["deficit"] is not JsonValue deficit)
        {
            return (0, false);
        }

        var value = deficit.AsNumber();
        return value >= 0 ? (value, true) : (0, false);
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
