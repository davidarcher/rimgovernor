using System.Text;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Defs;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go colony_gear.go, forecast.go, apparel_policy_read.go and the shared row checks they use: pawns.go (pawnsRef),
// combat_pawns.go (combatDetails and its helpers) and presentation_reads.go (presentationText).
public static partial class BridgeFunctions
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Go's <c>presentationText</c>: an absent text, or valid UTF-8 of at most <paramref name="limit"/> bytes.</summary>
    private static bool PresentationText(bool present, string value, int limit)
    {
        if (!present)
        {
            return true;
        }
        try
        {
            return StrictUtf8.GetByteCount(value) <= limit;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Go's <c>pawnsRef</c>: a CAS token that names <paramref name="id"/> and was read under, not after, <paramref name="context"/>.</summary>
    private static void PawnsRef(SnapshotRef snapshot, string id, ObservationContext context)
    {
        ValidateContext(snapshot.Context);
        if ((snapshot.EntityId ?? "") != id || !SameIdentity(snapshot.Context?.Identity, context.Identity) || (snapshot.Context?.Tick).GetValueOrDefault() > context.Tick.GetValueOrDefault())
        {
            throw Contract("pawn CAS context or entity mismatch");
        }
        ValidId((snapshot.Token ?? ""));
    }

    /// <summary>Go's <c>combatIDs</c>: whether every id is valid and none repeats.</summary>
    private static bool CombatIdsValid(IEnumerable<string> values) => ValidIds(values, true);

    private static void CombatIds(IEnumerable<string> values)
    {
        if (!CombatIdsValid(values))
        {
            throw Contract("invalid duplicate combat detail ID");
        }
    }

    /// <summary>Go's <c>combatDefName</c>: a definition name that is stated and valid.</summary>
    private static void CombatDefName(bool present, string value)
    {
        if (!present)
        {
            throw Contract("combat definition missing");
        }
        if (!IsValidId(value))
        {
            throw Contract("combat definition invalid");
        }
    }

    /// <summary>Go's <c>validBiocode</c>: a gear item biocoded to a valid pawn, and never stated as not biocoded.</summary>
    private static bool ValidBiocode(GearItem g) => g.BiocodedTo is null || (IsValidId((g.BiocodedTo ?? "")) && (g.Biocoded is null || g.Biocoded.GetValueOrDefault()));

    /// <summary>Go's <c>combatDetails</c>: the health, equipment, biography and animal facts of a pawn row.</summary>
    private static void CombatDetails(PawnState row, ObservationContext context)
    {
        if (!NonNegativeNumber(row.RaidArmor is not null, row.RaidArmor.GetValueOrDefault()))
        {
            throw Contract("invalid raid armor");
        }
        if (row.Health is { } h)
        {
            foreach (var (present, value) in new[]
            {
                (h.SummaryFraction is not null, h.SummaryFraction.GetValueOrDefault()),
                (h.BleedRatePerDay is not null, h.BleedRatePerDay.GetValueOrDefault()),
                (h.Pain is not null, h.Pain.GetValueOrDefault()),
                (h.BloodLoss is not null, h.BloodLoss.GetValueOrDefault()),
                (h.HoursUntilDeathFromBloodLoss is not null, h.HoursUntilDeathFromBloodLoss.GetValueOrDefault()),
            })
            {
                if (!NonNegativeNumber(present, value))
                {
                    throw Contract("invalid combat health number");
                }
            }
            if (h.SummaryFraction is not null && h.SummaryFraction.GetValueOrDefault() > 1)
            {
                throw Contract("invalid combat health fraction");
            }
            if (h.BedId is not null && !IsValidId((h.BedId ?? "")))
            {
                throw Contract("invalid combat bed");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in h.Capacities)
            {
                if (v is null || !IsValidId((v.DefName ?? "")) || seen.Contains((v.DefName ?? "")) || !NonNegativeNumber(v.Level is not null, v.Level.GetValueOrDefault()))
                {
                    throw Contract("invalid combat capacity");
                }
                seen.Add((v.DefName ?? ""));
                if (v.Unavailable is not null)
                {
                    if (v.Level is not null)
                    {
                        throw Contract("capacity known and unavailable");
                    }
                    ValidateUnavailable(v.Unavailable);
                }
            }
            foreach (var v in h.Hediffs)
            {
                if (v is null)
                {
                    throw Contract("missing hediff");
                }
                CombatDefName(v.DefName is not null, (v.DefName ?? ""));
                if ((v.PartDefName is not null && !IsValidId((v.PartDefName ?? "")))
                    || (v.PartIndex is not null && v.PartIndex.GetValueOrDefault() < 0)
                    || (v.TendExpiresInTicks is not null && v.TendExpiresInTicks.GetValueOrDefault() < 0)
                    || (v.NextTendInTicks is not null && v.NextTendInTicks.GetValueOrDefault() < 0))
                {
                    throw Contract("invalid hediff fields");
                }
                foreach (var (present, value) in new[]
                {
                    (v.Severity is not null, v.Severity.GetValueOrDefault()),
                    (v.TendQuality is not null, v.TendQuality.GetValueOrDefault()),
                    (v.Immunity is not null, v.Immunity.GetValueOrDefault()),
                    (v.SeverityPerDay is not null, v.SeverityPerDay.GetValueOrDefault()),
                    (v.ImmunityPerDay is not null, v.ImmunityPerDay.GetValueOrDefault()),
                })
                {
                    if (!FiniteNumber(present, value))
                    {
                        throw Contract("nonfinite hediff");
                    }
                }
            }
            seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in h.SurgeryBills)
            {
                if (v is null || !IsValidId((v.Id ?? "")) || seen.Contains((v.Id ?? "")) || (v.Recipe is not null && !IsValidId((v.Recipe ?? ""))) || (v.PartIndex is not null && v.PartIndex.GetValueOrDefault() < 0))
                {
                    throw Contract("invalid surgery bill");
                }
                seen.Add((v.Id ?? ""));
            }
            if (h.Snapshot is not null)
            {
                PawnsRef(h.Snapshot, row.Pawn?.Id ?? "", context);
            }
            PawnsIssues(h.Issues, h);
        }
        if (row.Equipment is { } e)
        {
            if ((e.PrimaryId is not null && !IsValidId((e.PrimaryId ?? ""))) || (e.CarriedThingId is not null && !IsValidId((e.CarriedThingId ?? ""))))
            {
                throw Contract("invalid equipment identity");
            }
            foreach (var list in new[] { e.Equipped, e.Apparel, e.InventoryWeapons })
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var g in list)
                {
                    if (g is null)
                    {
                        throw Contract("missing gear");
                    }
                    if (!ValidRef(g.Thing))
                    {
                        throw Contract("invalid gear reference");
                    }
                    if (!seen.Add((g.Thing.Id ?? "")))
                    {
                        throw Contract("duplicate gear");
                    }
                    if (g.Stuff is not null && !IsValidId((g.Stuff ?? "")))
                    {
                        throw Contract("invalid gear definition");
                    }
                    if (!ValidBiocode(g))
                    {
                        throw Contract("invalid gear biocode");
                    }
                    if ((g.HitPoints is not null && g.HitPoints.GetValueOrDefault() < 0) || (g.MaxHitPoints is not null && g.MaxHitPoints.GetValueOrDefault() <= 0))
                    {
                        throw Contract("invalid gear hit points");
                    }
                    foreach (var (present, value) in new[]
                    {
                        (g.ConditionFraction is not null, g.ConditionFraction.GetValueOrDefault()),
                        (g.ArmorSharp is not null, g.ArmorSharp.GetValueOrDefault()),
                        (g.ArmorBlunt is not null, g.ArmorBlunt.GetValueOrDefault()),
                        (g.InsulationCold is not null, g.InsulationCold.GetValueOrDefault()),
                        (g.InsulationHeat is not null, g.InsulationHeat.GetValueOrDefault()),
                    })
                    {
                        if (!FiniteNumber(present, value))
                        {
                            throw Contract("invalid gear number");
                        }
                    }
                }
            }
            if (e.Armed is not null && !e.Armed.GetValueOrDefault() && e.PrimaryId is not null)
            {
                throw Contract("unarmed pawn has primary");
            }
            if (e.InventoryItemCount is not null && e.InventoryItemCount.GetValueOrDefault() < (ulong)e.InventoryWeapons.Count)
            {
                throw Contract("inventory count mismatch");
            }
            PawnsIssues(e.Issues, e);
        }
        if (row.Biography is { } b)
        {
            if (!NonNegativeNumber(b.BiologicalAgeYears is not null, b.BiologicalAgeYears.GetValueOrDefault())
                || !NonNegativeNumber(b.ChronologicalAgeYears is not null, b.ChronologicalAgeYears.GetValueOrDefault())
                || !PresentationText(b.Title is not null, (b.Title ?? ""), 16384)
                || !PresentationText(b.TitleSource is not null, (b.TitleSource ?? ""), 16384))
            {
                throw Contract("invalid biography");
            }
            if (b.ChildhoodDefName is not null)
            {
                CombatDefName(true, (b.ChildhoodDefName ?? ""));
            }
            if (b.AdulthoodDefName is not null)
            {
                CombatDefName(true, (b.AdulthoodDefName ?? ""));
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in b.Skills)
            {
                if (v is null)
                {
                    throw Contract("missing skill");
                }
                CombatDefName(v.DefName is not null, (v.DefName ?? ""));
                if (!seen.Add((v.DefName ?? ""))
                    || (v.Level is not null && v.Level.GetValueOrDefault() < 0)
                    || !NonNegativeNumber(v.StoredLevel is not null, v.StoredLevel.GetValueOrDefault())
                    || (v.Passion is not null && PassionName(v.Passion.GetValueOrDefault()).Length == 0))
                {
                    throw Contract("invalid skill");
                }
            }

            // Trait degrees are signed native values; hediffs and traits can repeat.
            foreach (var v in b.Traits)
            {
                if (v is null || !IsValidId((v.DefName ?? "")))
                {
                    throw Contract("invalid trait");
                }
            }
            foreach (var list in new[] { b.IncapableWorkTypes, b.IncapableSources, b.DisabledWorkTags })
            {
                CombatIds(list);
            }
            PawnsIssues(b.Issues, b);
        }
        if (row.AnimalState is { } a)
        {
            // Training rows carry MaintainHerd's training deficit: one per trainable def.
            var trainables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in a.Training)
            {
                if (entry is null || !IsValidId((entry.DefName ?? "")) || trainables.Contains((entry.DefName ?? "")) || !PresentationText(entry.Reason is not null, (entry.Reason ?? ""), 4096))
                {
                    throw Contract("invalid animal training");
                }
                trainables.Add((entry.DefName ?? ""));
            }
            if ((a.PenId is not null && (!IsValidId((a.PenId ?? "")) || (a.Contained is not null && !a.Contained.GetValueOrDefault())))
                || (a.AllowedAreaId is not null && (a.AllowedAreaId ?? "").Length != 0 && !IsValidId((a.AllowedAreaId ?? ""))))
            {
                throw Contract("invalid animal herd facts");
            }
            PawnsIssues(a.Issues, a);
        }
    }

    /// <summary>Go's <c>validateApparelPolicy</c>: the outfit policy a gear row was planned under.</summary>
    private static void ValidateApparelPolicy(ApparelPolicyState? v)
    {
        if (v is null)
        {
            return;
        }
        if (!IsValidId((v.Token ?? "")) || v.Child is null || v.Slave is null || v.IncapableOfViolence is null || v.Drafted is null)
        {
            throw Contract("invalid apparel policy census");
        }
        if (!IsValidId((v.Name ?? ""))
            || v.MinHitPoints is null
            || v.MaxHitPoints is null
            || float.IsNaN(v.MinHitPoints.GetValueOrDefault())
            || float.IsNaN(v.MaxHitPoints.GetValueOrDefault())
            || v.MinHitPoints.GetValueOrDefault() < 0
            || v.MaxHitPoints.GetValueOrDefault() > 1
            || v.MinHitPoints.GetValueOrDefault() > v.MaxHitPoints.GetValueOrDefault()
            || v.MinQuality is null
            || v.MaxQuality is null
            || v.MinQuality.GetValueOrDefault() < 0
            || v.MaxQuality.GetValueOrDefault() > 6
            || v.MinQuality.GetValueOrDefault() > v.MaxQuality.GetValueOrDefault()
            || v.ExcludesTainted is null)
        {
            throw Contract("incomplete apparel policy");
        }

        // The outfit filter may allow apparel this pawn cannot wear (a child's or another body's): only ids and
        // duplicates are checked.
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in v.AllowedDefs)
        {
            if (!IsValidId(d) || !allowed.Add(d))
            {
                throw Contract("invalid apparel filter");
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in v.Work)
        {
            if (w is null
                || !IsValidId((w.DefName ?? ""))
                || seen.Contains((w.DefName ?? ""))
                || w.Priority is null
                || w.Priority.GetValueOrDefault() < 0
                || w.Priority.GetValueOrDefault() > DomainConstants.MaxPriority
                || w.Disabled is null)
            {
                throw Contract("invalid apparel work role");
            }
            seen.Add((w.DefName ?? ""));
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in v.Skills)
        {
            if (s is null || !IsValidId((s.DefName ?? "")) || seen.Contains((s.DefName ?? "")) || s.Level is null || s.Level.GetValueOrDefault() < 0 || s.Level.GetValueOrDefault() > 20 || s.Passion is null || s.Disabled is null)
            {
                throw Contract("invalid apparel skill census");
            }
            seen.Add((s.DefName ?? ""));
        }
    }

    /// <summary>Go's <c>validateColonyGear</c>: the planning gear census, stale or mismatched rows refused.</summary>
    private static void ValidateColonyGear(GearSnapshot? v, ObservationContext context)
    {
        if (v is null || !Equals(v.Context, context))
        {
            throw Contract("gear census context mismatch");
        }
        if (v.StoredApparel is { } stored)
        {
            var seen = new HashSet<(string, string, int, int)>();
            foreach (var row in stored.Rows)
            {
                if (row is null
                    || row.DefName is null
                    || !IsValidId((row.DefName ?? ""))
                    || row.Stuff is null
                    || ((row.Stuff ?? "").Length != 0 && !IsValidId((row.Stuff ?? "")))
                    || row.Quality is null
                    || row.Quality.GetValueOrDefault() < 0
                    || row.Quality.GetValueOrDefault() > 6
                    || row.HpBand is null
                    || row.HpBand.GetValueOrDefault() < 5
                    || row.HpBand.GetValueOrDefault() > 9
                    || row.Count is null
                    || row.Count.GetValueOrDefault() <= 0)
                {
                    throw Contract("invalid apparel storage row");
                }
                if (!seen.Add(((row.DefName ?? ""), (row.Stuff ?? ""), row.Quality.GetValueOrDefault(), row.HpBand.GetValueOrDefault())))
                {
                    throw Contract("duplicate apparel storage row");
                }
            }
        }
        if (v.OutdoorTemperatureByTwelfthC.Count != 0 || v.CurrentTwelfth is not null || v.TicksToNextTwelfth is not null || v.ActiveWeather is not null)
        {
            if (v.OutdoorTemperatureByTwelfthC.Count != 12
                || v.CurrentTwelfth is null
                || v.CurrentTwelfth.GetValueOrDefault() >= 12
                || v.TicksToNextTwelfth is null
                || v.TicksToNextTwelfth.GetValueOrDefault() <= 0
                || v.TicksToNextTwelfth.GetValueOrDefault() > 300000)
            {
                throw Contract("invalid gear seasonal curve");
            }
            if (v.OutdoorTemperatureByTwelfthC.Any(n => !float.IsFinite(n)))
            {
                throw Contract("invalid gear seasonal temperature");
            }
            if (v.ActiveWeather is { } w)
            {
                if (w.DefName is null
                    || ((w.DefName ?? "") != "ColdSnap" && (w.DefName ?? "") != "HeatWave")
                    || w.RemainingTicks is null
                    || w.RemainingTicks.GetValueOrDefault() < -1
                    || w.TemperatureOffsetC is null
                    || !float.IsFinite(w.TemperatureOffsetC.GetValueOrDefault())
                    || ((w.DefName ?? "") == "ColdSnap" && w.TemperatureOffsetC.GetValueOrDefault() > 0)
                    || ((w.DefName ?? "") == "HeatWave" && w.TemperatureOffsetC.GetValueOrDefault() < 0))
                {
                    throw Contract("invalid gear weather condition");
                }
            }
        }
        var people = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in v.Pawns)
        {
            if (p is null || p.Snapshot is null || !Equals(p.Snapshot.Context, context))
            {
                throw Contract("missing or stale gear loadout");
            }
            if (!ValidRef(p.Pawn))
            {
                throw Contract("invalid gear pawn");
            }
            var id = (p.Pawn.Id ?? "");
            if (!people.Add(id))
            {
                throw Contract("duplicate gear pawn");
            }
            PawnsRef(p.Snapshot, id, context);
            ValidateApparelPolicy(p.ApparelPolicy);
            ValidateGearWearer(p);
            if (!PresentationText(p.Blocker is not null, (p.Blocker ?? ""), 4096) || (p.Blocker is not null && (p.Blocker ?? "").Length == 0) || (p.Blocker is not null && p.Candidates.Count > 0))
            {
                throw Contract("invalid blocked gear loadout");
            }
            if (!FiniteNumber(p.ComfortableMinC is not null, p.ComfortableMinC.GetValueOrDefault())
                || !FiniteNumber(p.ComfortableMaxC is not null, p.ComfortableMaxC.GetValueOrDefault())
                || (p.ComfortableMinC is not null && p.ComfortableMaxC is not null && p.ComfortableMinC.GetValueOrDefault() > p.ComfortableMaxC.GetValueOrDefault()))
            {
                throw Contract("invalid gear temperature range");
            }
            CombatDetails(new PawnState { Equipment = p.Equipment }, context);
            var candidates = new List<GearItem>();
            foreach (var candidate in p.Candidates)
            {
                if (candidate is null
                    || candidate.Item is null
                    || candidate.Gain is null
                    || !NonNegativeNumber(candidate.Gain is not null, candidate.Gain.GetValueOrDefault())
                    || candidate.Gain.GetValueOrDefault() <= 0
                    || candidate.Blocker is not null
                    || candidate.Reason is not null)
                {
                    throw Contract("invalid eligible gear candidate");
                }
                candidates.Add(candidate.Item);
            }
            var carried = new PawnEquipment();
            carried = carried with { Equipped = [.. carried.Equipped, .. candidates] };
            CombatDetails(new PawnState { Equipment = carried }, context);
            if (p.LoadoutModel is null || p.ComfortableMinC is null || p.ComfortableMaxC is null)
            {
                throw Contract("gear loadout without a loadout model or comfortable temperatures");
            }
            ValidateGearModel(p.LoadoutModel);
        }
    }

    /// <summary>Go's <c>validateGearWearer</c>: the wear inputs of a pawn row, its gender, developmental stage and body part groups.</summary>
    private static void ValidateGearWearer(GearLoadout p)
    {
        if (p.DevelopmentalStage is not (DevelopmentalStage.Newborn or DevelopmentalStage.Baby or DevelopmentalStage.Child or DevelopmentalStage.Adult))
        {
            throw Contract("invalid gear wearer stage");
        }
        if (p.Gender is null || !Enum.IsDefined(p.Gender.GetValueOrDefault()) || !CombatIdsValid(p.BodyPartGroups))
        {
            throw Contract("invalid gear wearer");
        }
    }

    /// <summary>
    /// Go's <c>validateGearModel</c>: the loadout model's wire shape, its identities, source vocabulary and finite
    /// stats. Policy checks the model's bounds and conflicts when the row is mapped.
    /// </summary>
    private static void ValidateGearModel(GearLoadoutModel? m)
    {
        if (m is null)
        {
            return;
        }
        if (m.Traits.Any(t => t is null || !IsValidId((t.DefName ?? ""))))
        {
            throw Contract("invalid gear model trait");
        }
        foreach (var list in new[] { m.Worn, m.Options })
        {
            foreach (var x in list)
            {
                if (x is null || !IsValidId((x.Id ?? "")) || !IsValidId((x.DefName ?? "")) || (x.Stuff is not null && !IsValidId((x.Stuff ?? ""))) || x.Quality is null || x.Condition is null)
                {
                    throw Contract("invalid gear model option");
                }
                if (x.Source is not ("worn" or "loose" or "stored" or "bill"))
                {
                    throw Contract("invalid gear model option source");
                }
                if (!FiniteNumber(x.Condition is not null, x.Condition.GetValueOrDefault()))
                {
                    throw Contract("invalid gear model option condition");
                }
            }
        }
    }

    /// <summary>
    /// Go's <c>ValidateForecast</c>: the forecast's combined food supply, its animal partition against the human
    /// supply, and the crop and patient rows.
    /// </summary>
    /// <exception cref="BridgeContractException">The forecast is malformed.</exception>
    public static void ValidateForecast(ForecastFacts? v, FoodSupplyFacts? human)
    {
        if (v is null)
        {
            throw Contract("forecast exceeds bound");
        }
        BuildingUnknown(v);
        ValidateFoodSupply(v.CombinedFoodSupply);
        var consumers = new HashSet<string>(StringComparer.Ordinal);
        var rates = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var row in (v.CombinedFoodSupply ?? throw Contract("forecast exceeds bound")).Consumers)
        {
            consumers.Add((row.PawnId ?? ""));
            rates[(row.PawnId ?? "")] = OptionalDouble(row.NutritionPerDay is not null, row.NutritionPerDay.GetValueOrDefault());
        }
        var partition = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in v.AnimalIds)
        {
            if (!consumers.Contains(id) || !partition.Add(id))
            {
                throw Contract("invalid animal census");
            }
        }
        if (human is not null)
        {
            ValidateFoodSupply(human);
            foreach (var row in human.Consumers)
            {
                var id = (row.PawnId ?? "");
                if (!consumers.Contains(id) || !partition.Add(id))
                {
                    throw Contract("inconsistent combined consumers");
                }
                if (rates[id] is { } rate && row.NutritionPerDay is not null && rate != row.NutritionPerDay.GetValueOrDefault())
                {
                    throw Contract("conflicting human food demand");
                }
            }
            if (partition.Count != consumers.Count)
            {
                throw Contract("unclassified combined consumer");
            }
        }
        var zones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Crops)
        {
            if (row is null
                || !IsValidId(row.Zone?.Id ?? "")
                || zones.Contains((row.Zone!.Id ?? ""))
                || (row.Crop is not null && !IsValidId((row.Crop ?? "")))
                || (row.Product is not null && !IsValidId((row.Product ?? ""))))
            {
                throw Contract("invalid crop forecast");
            }
            zones.Add((row.Zone.Id ?? ""));
            PawnsIssues(row.Issues, row);
            if (!NonNegativeNumber(row.SowWork is not null, row.SowWork.GetValueOrDefault())
                || !NonNegativeNumber(row.HarvestWork is not null, row.HarvestWork.GetValueOrDefault())
                || !NonNegativeNumber(row.StandingYield is not null, row.StandingYield.GetValueOrDefault()))
            {
                throw Contract("invalid crop quantity");
            }
        }
        var patients = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Patients)
        {
            if (row is null || !IsValidId((row.PawnId ?? "")) || !patients.Add((row.PawnId ?? "")))
            {
                throw Contract("invalid patient forecast");
            }
            PawnsIssues(row.Issues, row);
            if (!NonNegativeNumber(row.BleedRatePerDay is not null, row.BleedRatePerDay.GetValueOrDefault()) || !NonNegativeNumber(row.HoursUntilDeathFromBloodLoss is not null, row.HoursUntilDeathFromBloodLoss.GetValueOrDefault()))
            {
                throw Contract("invalid medical quantity");
            }
            if (!FiniteNumber(row.Mood is not null, row.Mood.GetValueOrDefault())
                || !FiniteNumber(row.MoodTarget is not null, row.MoodTarget.GetValueOrDefault())
                || !FiniteNumber(row.MinorBreakThreshold is not null, row.MinorBreakThreshold.GetValueOrDefault())
                || !FiniteNumber(row.MajorBreakThreshold is not null, row.MajorBreakThreshold.GetValueOrDefault())
                || !FiniteNumber(row.ExtremeBreakThreshold is not null, row.ExtremeBreakThreshold.GetValueOrDefault()))
            {
                throw Contract("invalid mood quantity");
            }
        }
        if (human is not null && human.Consumers.Any(row => !patients.Contains((row.PawnId ?? ""))))
        {
            throw Contract("human patient forecast missing");
        }
        if (v.AnimalIds.Any(patients.Contains))
        {
            throw Contract("animal in human patient census");
        }
    }
}
