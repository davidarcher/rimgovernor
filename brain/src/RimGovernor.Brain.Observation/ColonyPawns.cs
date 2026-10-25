namespace RimGovernor.Brain.Observation;

/// <summary>The pawn-derived sections shared by routine and direct colony readings.</summary>
public static class ColonyPawnReads
{
    internal static ColonyProjection WithPawnCensus(ColonyProjection projection, O.ColonyFactsSnapshot colony, Tables tables, DefinitionCatalog catalog)
    {
        var source = tables.PawnCensus ?? throw new BridgeContractException("missing total pawn census");
        var rows = source.Pawns.Where(p => p.FreeColonist && !p.Dead).ToArray();
        if (rows.LongLength != colony.ColonistCount || rows.Select(p => p.Pawn.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
        {
            throw new ObservationContractException("colony and pawn colonist censuses disagree");
        }
        var pawns = source with { Pawns = EquatableArray.From(rows) };
        var emergency = tables.Emergency ?? throw new BridgeContractException("missing total emergency census");
        var (armed, unarmed) = RoundsMoodReads.RoundsArmed(colony, emergency, pawns, catalog.CreepJoinerDownsides());
        var work = RoundsWorkReads.RoundsWork(colony, emergency, pawns, catalog, tables.Things);
        var mood = RoundsMoodReads.RoundsMood(colony, emergency, pawns);
        var medical = RoundsContainmentReads.RoundsMedical(colony, emergency, pawns, catalog);
        var (critical, urgent) = EmergencyNeedsRules.PatientCounts(emergency, medical);
        return projection with
        {
            WorkPawns = work,
            Rest = RoundsWorkReads.RoundsRest(pawns),
            MechCatalog = catalog.Biotech?.MechCatalog() ?? new(),
            Facts = projection.Facts with
            {
                Armed = armed,
                Unarmed = unarmed,
                DefenseCapacity = DefenseCapacityPolicy.DefenseCapacity(RoundsMoodReads.RoundsDefenders(colony, emergency, pawns), projection.DefenseTurrets),
                MedicalPawns = medical,
                CriticalPatients = critical,
                UrgentPatients = urgent,
                MedicalCareRecovered = MedicalRules.ReviewMedicalCare(medical, new()).Recovered(),
                ForecastObserved = projection.Facts.ForecastObserved with { WorkPawns = work, Needs = colony.Forecast.Needs, Coupled = colony.Forecast.Coupled },
                MoodPawns = mood,
                MoodLedger = RoundsMoodReads.RoundsMoodLedger(mood, pawns, catalog.AllThoughtFacts()),
                WorkProfiles = [.. PawnRules.Profiles(work.Pawns)],
                MedicalReserve = projection.Facts.MedicalReserve with { Catalog = catalog.ItemFacts() },
            },
        };
    }
}