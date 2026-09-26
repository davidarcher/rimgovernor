using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;
using Obs = RimGovernor.Protocol.Observations;

// Filter-first threat classification (#646). The status read's legacy output
// contract is written out here case by case -- which list each pawn lands in,
// its reason, its nearest-colonist distance and its hunt fields -- and the
// production classifier runs against it with counted projectors, so the
// expected results never come from the classifier itself. The call counts,
// not wall time, prove that discarded wildlife is never projected.
internal static class NativeThreatClassifierProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private sealed class P { internal string Id; internal ThreatFacts F; }
    private static P Pawn(string id, ThreatFacts f) { return new P { Id = id, F = f }; }

    private static int projected;
    private static readonly List<string> ProjectedIds = new List<string>();

    private static (Obs.ThreatsSnapshot, NativeThreatClassifier.Scan) Run(List<P> pawns, List<(int X, int Z)> colonists, double radius)
    {
        projected = 0; ProjectedIds.Clear();
        var threats = new Obs.ThreatsSnapshot();
        var hop = ObservationWork.Begin();
        var scan = NativeThreatClassifier.Collect(pawns, p => p.F, colonists, radius,
            p => { projected++; ProjectedIds.Add(p.Id); return new Obs.PawnState { Pawn = new Obs.EntityRef { Id = p.Id } }; },
            p => new Obs.EntityRef { Id = "prey-" + p.Id }, threats);
        ObservationWork.End();
        var report = ObservationWork.Report(hop);
        var account = (Dictionary<string, object>)report["threatScan"];
        Check((long)account["examined"] == scan.Examined && (long)account["projections"] == scan.Projections
            && (long)account["candidates"] == scan.Candidates && (long)account["proximityChecks"] == scan.ProximityChecks, "hop account carries the scan");
        return (threats, scan);
    }

    private static string Ids(IEnumerable<Obs.ThreatPawn> rows) { return string.Join(",", rows.Select(r => r.Pawn.Pawn.Id)); }

    internal static void Invoke()
    {
        LegacyContract();
        NoColonists();
        ZeroRadius();
        Console.WriteLine("native-threat-classifier: " + checks + " checks passed");
    }

    private static readonly List<(int X, int Z)> Colonists = new List<(int X, int Z)> { (0, 0), (10, 0) };

    private static void LegacyContract()
    {
        var pawns = new List<P> {
            Pawn("raider", new ThreatFacts { FactionHostile = true, FactionId = "Faction_9", X = 50, Z = 0 }),
            Pawn("wolf", new ThreatFacts { Mental = "ManhunterPermanent", Predator = true, X = 100, Z = 100 }),
            Pawn("hostileDowned", new ThreatFacts { FactionHostile = true, FactionId = "Faction_9", Downed = true, Predator = true, PredatorHunt = true, X = 1, Z = 1 }),
            Pawn("ourHunter", new ThreatFacts { Ours = true, PredatorHunt = true, HasPrey = true, PreyOurs = false, Predator = true, X = 200, Z = 0 }),
            Pawn("wildOnDeer", new ThreatFacts { PredatorHunt = true, HasPrey = true, PreyOurs = false, Predator = true, X = 60, Z = 0 }),
            Pawn("wildOnCorpse", new ThreatFacts { PredatorHunt = true, HasPrey = true, PreyOurs = true, Predator = true, X = 12, Z = 7 }),
            Pawn("wildNoPrey", new ThreatFacts { PredatorHunt = true, Predator = true, X = -5, Z = 0 }),
            Pawn("neutralDowned", new ThreatFacts { Downed = true, X = 5, Z = 5 }),
            Pawn("downedPredator", new ThreatFacts { Downed = true, Predator = true, X = 3, Z = 0 }),
            Pawn("edgePredator", new ThreatFacts { Predator = true, X = 40, Z = 0 }),
            Pawn("farPredator", new ThreatFacts { Predator = true, X = 41, Z = 0 }),
            Pawn("ourDowned", new ThreatFacts { Ours = true, Downed = true, Predator = true, X = 1, Z = 0 }),
        };
        for (var i = 0; i < 500; i++) pawns.Add(Pawn("hare" + i, new ThreatFacts { X = i % 7, Z = i % 5 }));
        var (t, scan) = Run(pawns, Colonists, 30);

        Check(Ids(t.Hostiles) == "raider,wolf,hostileDowned", "hostiles in scan order: " + Ids(t.Hostiles));
        Check(t.Hostiles[0].Pawn.HostileReason == "faction:Faction_9" && t.Hostiles[0].Pawn.NearestColonistDistance == 40 && t.Hostiles[0].Pawn.Hostile, "faction hostile");
        Check(t.Hostiles[1].Pawn.HostileReason == "manhunter:ManhunterPermanent" && t.Hostiles[1].Pawn.NearestColonistDistance == 100, "manhunter wins over faction");
        Check(t.Hostiles[2].Prey == null && !t.Hostiles[2].HasPredatorIsOurs, "hostility precedes the hunt branch");
        Check(Ids(t.IgnoredHunters) == "ourHunter,wildOnDeer", "ignored hunters: " + Ids(t.IgnoredHunters));
        Check(t.IgnoredHunters[0].IgnoredReason == "player-owned predator" && t.IgnoredHunters[0].PredatorIsOurs && t.IgnoredHunters[0].Prey.Id == "prey-ourHunter" && !t.IgnoredHunters[0].PreyIsOurs, "player-owned hunter");
        Check(t.IgnoredHunters[1].IgnoredReason == "prey is not player-owned" && !t.IgnoredHunters[1].PredatorIsOurs && t.IgnoredHunters[1].Pawn.HostileReason == "predatorHunt" && t.IgnoredHunters[1].Pawn.NearestColonistDistance == 50, "hunter on non-player prey");
        Check(Ids(t.HuntingPredators) == "wildOnCorpse,wildNoPrey", "hunting predators: " + Ids(t.HuntingPredators));
        Check(t.HuntingPredators[0].PreyIsOurs && t.HuntingPredators[0].Prey.Id == "prey-wildOnCorpse" && t.HuntingPredators[0].Pawn.NearestColonistDistance == 7, "hunt on our corpse");
        Check(t.HuntingPredators[1].Prey == null && !t.HuntingPredators[1].HasPreyIsOurs && !t.HuntingPredators[1].HasIgnoredReason && t.HuntingPredators[1].Pawn.HostileReason == "predatorHunt", "missing prey is hunting");
        Check(Ids(t.DownedNear) == "neutralDowned,downedPredator", "downed near: " + Ids(t.DownedNear));
        Check(t.DownedNear.All(r => r.Pawn.HostileReason == "downed" && r.Pawn.HasHostile && !r.Pawn.Hostile), "downed reasons");
        Check(t.DownedNear[0].Pawn.NearestColonistDistance == 5, "downed distance");
        Check(Ids(t.WildPredatorsNear) == "downedPredator,edgePredator", "predators near (radius inclusive): " + Ids(t.WildPredatorsNear));
        Check(t.WildPredatorsNear.All(r => r.Pawn.HostileReason == "predator_near"), "predator reasons");
        Check(!ReferenceEquals(t.DownedNear[1].Pawn, t.WildPredatorsNear[0].Pawn), "a pawn in two lists shares no mutable row");
        Check(t.WildPredatorsNear[1].Pawn.NearestColonistDistance == 30, "edge distance");

        // Eleven rows from ten kept pawns; the 500 hares, the far predator
        // and our own downed animal are never projected, and only the far
        // predator of the discards needed a distance scan.
        Check(projected == 10 && scan.Projections == 10 && scan.Candidates == 10, "projections: " + projected);
        Check(!ProjectedIds.Any(id => id.StartsWith("hare", StringComparison.Ordinal) || id == "farPredator" || id == "ourDowned"), "discarded pawns are never projected");
        Check(scan.Examined == 512, "examined");
        Check(scan.ProximityChecks == 11, "proximity checks: " + scan.ProximityChecks);
    }

    // With no colonists nothing has a distance, the proximity lists are
    // empty and the hostile and hunt lists are unchanged.
    private static void NoColonists()
    {
        var pawns = new List<P> {
            Pawn("raider", new ThreatFacts { FactionHostile = true, FactionId = "F", X = 1, Z = 1 }),
            Pawn("wildNoPrey", new ThreatFacts { PredatorHunt = true, Predator = true }),
            Pawn("downedPredator", new ThreatFacts { Downed = true, Predator = true }),
        };
        var (t, scan) = Run(pawns, new List<(int X, int Z)>(), 30);
        Check(t.Hostiles.Count == 1 && !t.Hostiles[0].Pawn.HasNearestColonistDistance, "no distance without colonists");
        Check(t.HuntingPredators.Count == 1 && !t.HuntingPredators[0].Pawn.HasNearestColonistDistance, "hunt kept without colonists");
        Check(t.DownedNear.Count == 0 && t.WildPredatorsNear.Count == 0, "nothing is near without colonists");
        Check(projected == 2 && scan.ProximityChecks == 0, "no projection or scan for the unplaceable");
    }

    // A zero radius keeps hostiles and hunts and discards the proximity
    // branch without scanning.
    private static void ZeroRadius()
    {
        var pawns = new List<P> {
            Pawn("raider", new ThreatFacts { FactionHostile = true, FactionId = "F", X = 0, Z = 0 }),
            Pawn("downedPredator", new ThreatFacts { Downed = true, Predator = true, X = 0, Z = 0 }),
        };
        var (t, scan) = Run(pawns, Colonists, 0);
        Check(t.Hostiles.Count == 1 && t.Hostiles[0].Pawn.NearestColonistDistance == 0, "hostile at distance zero");
        Check(t.DownedNear.Count == 0 && t.WildPredatorsNear.Count == 0, "zero radius keeps no proximity threat");
        Check(projected == 1 && scan.ProximityChecks == 1, "zero radius skips the proximity scan");
    }
}
