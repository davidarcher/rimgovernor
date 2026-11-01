using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    internal const long DefenseReverifyTicks = GameTime.TicksPerHour;

    internal static string DefenseTurretRung(ReviewReading read)
    {
        var facts = read.Reading.Facts;
        foreach (var rung in DefenseTurretsPolicy.TurretRungs(ArmoryRules.AssessArmory(facts).Tier))
        {
            if (DefenseDefinitionAvailable(read, rung))
            {
                return rung;
            }
        }
        return DefenseTurretsPolicy.TurretMini;
    }

    internal static bool DefenseReverifyDue(DefenseVerified verified, Tick tick, string combat) =>
        combat != verified.Combat || tick.Value - verified.Tick.Value >= DefenseReverifyTicks;

    /// <summary>The plan of the map's latest fight (<c>engage-&lt;map&gt;</c>'s latest attempt), or empty when there is none.</summary>
    internal static string DefenseCombatKey(Player p, ReviewStep review, CancellationToken ct)
    {
        var owner = p.Journal.LoadNeed(MilitaryFamily.Combat, review.Snapshot, ct);
        return p.Journal.OwnerMethodPlan(owner, DefensePlanner.EngageMethod(review.Snapshot), ct)?.Value ?? "";
    }

    internal static bool DefenseTierCensusUpdate(DefenseLayoutRecord record, DefenseCensus census)
    {
        var gone = record.Tiers.Count;
        record.Tiers.RemoveAll(t => t.Remove && DefenseRemovalGone(t, census));
        var changed = gone != record.Tiers.Count;
        foreach (var tier in record.Tiers.ToList())
        {
            if (tier.Buildings.Count == 0 || tier.Remove)
            {
                continue;
            }
            var standing = true;
            foreach (var b in tier.Buildings)
            {
                standing = standing && census.Standing(b.Definition, b.Cell);
            }
            if (standing == tier.Built)
            {
                continue;
            }
            tier.Built = standing;
            record.SetTier(tier);
            changed = true;
        }
        return changed;
    }

    /// <summary>The census of the cells the tiers of <paramref name="scope"/> place; null when they place nothing or the plan has no killbox.</summary>
    internal async Task<DefenseCensus?> ReadTierCensusAsync(ControlState state, ReviewReading read, DefenseLayoutRecord scope, CancellationToken call)
    {
        if (!scope.Tiers.Any(t => t.Buildings.Count > 0))
        {
            return null;
        }
        var projection = read.Reading;
        var census = new DefenseCensus();
        var (_, baseCell, _, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return null;
        }
        var site = await DefenseSiteAsync(Boundary.Identity(state.Snapshot), projection, DefenseCensusRegion(scope, projection.Facts.MapBounds, baseCell), call).ConfigureAwait(false);
        foreach (var cell in site)
        {
            census.Fogged[cell.Cell] = cell is RimGovernor.Brain.GameFacts.Observations.DefenseCell.Fogged;
            if (cell is RimGovernor.Brain.GameFacts.Observations.DefenseCell.Seen seen)
            {
                census.Edifice[cell.Cell] = seen.Edifice?.DefName ?? "";
                census.EdificeStuff[cell.Cell] = seen.Edifice?.Stuff ?? "";
                census.Terrain[cell.Cell] = seen.Terrain;
                census.Cover[cell.Cell] = seen.Cover;
                census.Unbridging[cell.Cell] = seen.FoundationRemovalDesignated;
            }
        }
        foreach (var c in projection.Facts.EssentialInputs.Power.Conduits)
        {
            census.Conduits[c] = true;
        }
        foreach (var b in projection.Facts.EssentialInputs.Power.Buildings)
        {
            census.Consumers[b.Cell] = b;
        }
        return census;
    }

    /// <summary>Settles the record's tiers on the census: a finished removal leaves, a tier reads built when all of it stands.</summary>
    internal void SettleTierCensus(DefenseLayoutRecord record, DefenseCensus? census, CancellationToken call)
    {
        if (census is null)
        {
            return;
        }
        LogUnbuiltTiers(record, census, call);
        if (DefenseTierCensusUpdate(record, census))
        {
            _reviewer.Player.Journal.SaveDefenseLayout(record, call);
        }
    }
}

partial class DefenseLayoutPlanner
{
    /// <summary>
    /// The layout/defense Need's facts (#3314): whether the step compared the defense alternatives in this process (the
    /// ColonyReview's in-memory verification, #3008) at the map's latest fight within the re-verify window, the protection
    /// the chosen alternative still owed at that comparison, and whether an open fight waits in rooms to harden. Combat is
    /// read as facts only; no combat Need gates the layout.
    /// </summary>
    internal static DefenseLayoutFacts DefenseLayoutFactsOf(ColonyReview reviewer, ReviewStep review, CancellationToken ct)
    {
        var p = reviewer.Player;
        var world = new StoreWorld { Colony = review.Snapshot.Colony, Load = review.Snapshot.Load, Map = review.Snapshot.Map };
        var verified = reviewer.DefenseVerification.Get(world);
        var combat = DefenseLayoutPlanner.DefenseCombatKey(p, review, ct);
        return new DefenseLayoutFacts
        {
            Verified = combat != "" && verified.Complete && !DefenseLayoutPlanner.DefenseReverifyDue(verified, review.Tick, combat),
            Deficit = verified.Deficit,
            WaitingFight = DefenseWaitingFight(p.Journal, world, ct) is not null,
        };
    }
}
