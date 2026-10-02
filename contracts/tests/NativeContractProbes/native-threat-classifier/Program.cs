using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;
using Obs = RimGovernor.Protocol.Observations;

// Filter-first threat fact rows (#646, #1356). Which pawns get a row, its
// facts and its nearest-colonist distance are written out here case by case
// and the production filter runs against them with counted projectors, so
// the expected results never come from the filter itself. The call counts,
// not wall time, prove that discarded wildlife is never projected. The
// classification of these rows is Go's (bridge.ClassifyThreat, whose tests
// port the verdicts these cases used to assert).
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
            p => { projected++; ProjectedIds.Add(p.Id); return new RimGovernor.Protocol.Common.Ref { Id = p.Id }; },
            p => new Obs.EntityRef { Id = "prey-" + p.Id }, threats);
        ObservationWork.End();
        var report = ObservationWork.Report(hop);
        var account = (Dictionary<string, object>)report["threatScan"];
        Check((long)account["examined"] == scan.Examined && (long)account["projections"] == scan.Projections
            && (long)account["candidates"] == scan.Candidates && (long)account["proximityChecks"] == scan.ProximityChecks, "hop account carries the scan");
        return (threats, scan);
    }

    private static string Ids(IEnumerable<Obs.ThreatPawn> rows) { return string.Join(",", rows.Select(r => r.Pawn.Id)); }
    private static Obs.ThreatPawn Row(Obs.ThreatsSnapshot t, string id) { return t.Pawns.Single(r => r.Pawn.Id == id); }

    internal static void Invoke()
    {
        Facts();
        NoColonists();
        ZeroRadius();
        Passive();
        PrisonBreak();
        Console.WriteLine("native-threat-classifier: " + checks + " checks passed");
    }

    private static readonly List<(int X, int Z)> Colonists = new List<(int X, int Z)> { (0, 0), (10, 0) };

    private static void Facts()
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

        Check(Ids(t.Pawns) == "raider,wolf,hostileDowned,ourHunter,wildOnDeer,wildOnCorpse,wildNoPrey,neutralDowned,downedPredator,edgePredator", "rows in scan order: " + Ids(t.Pawns));
        var raider = Row(t, "raider");
        Check(raider.FactionHostile && raider.FactionId == "Faction_9" && raider.NearestColonistDistance == 40 && !raider.HasMentalState && !raider.HasPassive, "faction hostile facts");
        var wolf = Row(t, "wolf");
        Check(wolf.MentalState == "ManhunterPermanent" && wolf.Predator && !wolf.FactionHostile && !wolf.HasFactionId && wolf.NearestColonistDistance == 100, "manhunter facts");
        var hd = Row(t, "hostileDowned");
        Check(hd.FactionHostile && hd.Downed && hd.Predator && hd.PredatorHunt && hd.Prey == null && !hd.HasPreyIsOurs, "every fact rides together");
        var ourHunter = Row(t, "ourHunter");
        Check(ourHunter.Ours && ourHunter.PredatorHunt && ourHunter.Prey.Id == "prey-ourHunter" && ourHunter.HasPreyIsOurs && !ourHunter.PreyIsOurs, "player-owned hunter facts");
        Check(Row(t, "wildOnDeer").NearestColonistDistance == 50 && !Row(t, "wildOnDeer").Ours, "hunter distance");
        var corpse = Row(t, "wildOnCorpse");
        Check(corpse.PreyIsOurs && corpse.Prey.Id == "prey-wildOnCorpse" && corpse.NearestColonistDistance == 7, "hunt on our corpse");
        Check(Row(t, "wildNoPrey").Prey == null && !Row(t, "wildNoPrey").HasPreyIsOurs, "missing prey");
        Check(Row(t, "neutralDowned").Downed && Row(t, "neutralDowned").NearestColonistDistance == 5, "downed distance");
        Check(Row(t, "downedPredator").Downed && Row(t, "downedPredator").Predator, "downed predator is one row");
        Check(Row(t, "edgePredator").NearestColonistDistance == 30, "radius inclusive");

        // The 500 hares, the far predator and our own downed animal are
        // never projected, and only the far predator of the discards needed
        // a distance scan.
        Check(projected == 10 && scan.Projections == 10 && scan.Candidates == 10, "projections: " + projected);
        Check(!ProjectedIds.Any(id => id.StartsWith("hare", StringComparison.Ordinal) || id == "farPredator" || id == "ourDowned"), "discarded pawns are never projected");
        Check(scan.Examined == 512, "examined");
        Check(scan.ProximityChecks == 11, "proximity checks: " + scan.ProximityChecks);
    }

    // With no colonists nothing has a distance and nothing is near; engaged
    // pawns keep their rows.
    private static void NoColonists()
    {
        var pawns = new List<P> {
            Pawn("raider", new ThreatFacts { FactionHostile = true, FactionId = "F", X = 1, Z = 1 }),
            Pawn("wildNoPrey", new ThreatFacts { PredatorHunt = true, Predator = true }),
            Pawn("downedPredator", new ThreatFacts { Downed = true, Predator = true }),
        };
        var (t, scan) = Run(pawns, new List<(int X, int Z)>(), 30);
        Check(Ids(t.Pawns) == "raider,wildNoPrey" && t.Pawns.All(r => !r.HasNearestColonistDistance), "no distance without colonists");
        Check(projected == 2 && scan.ProximityChecks == 0, "no projection or scan for the unplaceable");
    }

    // The passive fact (#948, #1335) rides on the rows that carry it.
    private static void Passive()
    {
        var pawns = new List<P> {
            Pawn("dormantSpider", new ThreatFacts { FactionHostile = true, FactionId = "Insect", Passive = true, X = 3, Z = 0 }),
            Pawn("angrySpider", new ThreatFacts { FactionHostile = true, FactionId = "Insect", Passive = false, X = 4, Z = 0 }),
            Pawn("raider", new ThreatFacts { FactionHostile = true, FactionId = "F", X = 5, Z = 0 }),
        };
        var (t, _) = Run(pawns, Colonists, 30);
        Check(t.Pawns.Count == 3 && t.Pawns[0].HasPassive && t.Pawns[0].Passive, "dormant insect is passive");
        Check(t.Pawns[1].HasPassive && !t.Pawns[1].Passive, "engaging insect is not passive");
        Check(!t.Pawns[2].HasPassive, "a raider carries no passive fact");
    }

    // A zero radius keeps engaged pawns and discards the proximity branch
    // without scanning.
    private static void ZeroRadius()
    {
        var pawns = new List<P> {
            Pawn("raider", new ThreatFacts { FactionHostile = true, FactionId = "F", X = 0, Z = 0 }),
            Pawn("downedPredator", new ThreatFacts { Downed = true, Predator = true, X = 0, Z = 0 }),
        };
        var (t, scan) = Run(pawns, Colonists, 0);
        Check(Ids(t.Pawns) == "raider" && t.Pawns[0].NearestColonistDistance == 0, "engaged pawn at distance zero");
        Check(projected == 1 && scan.ProximityChecks == 1, "zero radius skips the proximity scan");
    }

    // A prison-breaking prisoner (#1080) carries its own fact; a held
    // prisoner of a hostile faction arrives with FactionHostile false.
    private static void PrisonBreak()
    {
        var pawns = new List<P> {
            Pawn("escapee", new ThreatFacts { PrisonBreak = true, X = 2, Z = 0 }),
            Pawn("held", new ThreatFacts { X = 3, Z = 0 }),
        };
        var (t, _) = Run(pawns, Colonists, 30);
        Check(Ids(t.Pawns) == "escapee" && t.Pawns[0].PrisonBreak && !t.Pawns[0].FactionHostile, "escapee carries the prison break fact");
    }
}
