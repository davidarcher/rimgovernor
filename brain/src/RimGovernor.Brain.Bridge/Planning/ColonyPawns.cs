using Identity = RimGovernor.Brain.GameAccess.ObservationIdentity;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.Bridge;

/// <summary>The pawn-derived sections shared by routine and direct colony readings.</summary>
public static class ColonyPawnReads
{
    internal static ColonyReading WithPawnCensus(ColonyReading projection, RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot colony, Tables tables, DefinitionCatalog catalog)
    {
        var source = tables.PawnCensus ?? throw new BridgeContractException("missing total pawn census");
        var rows = source.Pawns.Where(p => p.FreeColonist && !p.Dead).ToArray();
        if (rows.LongLength != colony.ColonistCount || rows.Select(p => p.Pawn.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
        {
            throw new ObservationContractException("colony and pawn colonist censuses disagree");
        }
        var pawns = source with { Pawns = EquatableArray.From(rows) };
        var emergency = tables.Emergency ?? throw new BridgeContractException("missing total emergency census");
        var (armed, unarmed) = MoodReads.Armed(colony, emergency, pawns, catalog.CreepJoinerDownsides());
        var work = WorkReads.Work(colony, emergency, pawns, catalog, tables.Things);
        var mood = MoodReads.Mood(colony, emergency, pawns);
        var medical = ContainmentReads.Medical(colony, emergency, pawns, catalog);
        var (critical, urgent) = EmergencyNeedsRules.PatientCounts(emergency, medical);
        return projection with
        {
            Facts = projection.Facts with
            {
                Mechs = ContainmentReads.MechFleetOf(tables.Pawns.Table.Values()),
                Rest = WorkReads.Rest(pawns),
                MechCatalog = catalog.Biotech?.MechCatalog() ?? new(),
                Armed = armed,
                Unarmed = unarmed,
                Hostiles = EmergencyNeedsRules.HostilesOf(EmergencyRules.EvaluateFacts(emergency), emergency.PodsOpen.Value > 0 && emergency.PodsOpen >= projection.Identity.Tick),
                DangerSeeds = EquatableArray.From(ShelterRules.DangerSeeds(emergency.Threats)),
                DangerHaulers = ShelterRules.DangerHaulers(work),
                DefenseCapacity = DefenseCapacityPolicy.DefenseCapacity(MoodReads.Defenders(colony, emergency, pawns), projection.Facts.EssentialInputs.Power.Buildings.Where(turret => turret.Turret is not null).Select(turret => (turret.Dps, turret.Powered))),
                MedicalPawns = medical,
                CriticalPatients = critical,
                UrgentPatients = urgent,
                MedicalCareRecovered = MedicalRules.ReviewMedicalCare(medical, new()).Recovered(),
                ForecastObserved = projection.Facts.ForecastObserved with { WorkPawns = work, Needs = colony.Forecast.Needs, Coupled = colony.Forecast.Coupled },
                MoodPawns = mood,
                MoodLedger = MoodRules.BuildMoodLedger(mood, catalog.AllThoughtFacts()),
                WorkProfiles = [.. PawnRules.Profiles(work.Pawns)],
                MedicalReserve = projection.Facts.MedicalReserve with { Catalog = catalog.ItemFacts() },
            },
        };
    }
}
