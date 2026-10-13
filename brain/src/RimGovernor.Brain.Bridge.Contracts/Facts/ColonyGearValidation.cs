using System.Text;
using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Defs;
using RimGovernor.Protocol.Observations;

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
        if (snapshot.EntityId != id || !SameIdentity(snapshot.Context.Identity, context.Identity) || snapshot.Context.Tick > context.Tick)
        {
            throw Contract("pawn CAS context or entity mismatch");
        }
        ValidId(snapshot.Token);
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
    private static bool ValidBiocode(GearItem g) => !g.HasBiocodedTo || (IsValidId(g.BiocodedTo) && (!g.HasBiocoded || g.Biocoded));

    /// <summary>Go's <c>combatDetails</c>: the health, equipment, biography and animal facts of a pawn row.</summary>
    private static void CombatDetails(PawnState row, ObservationContext context)
    {
        if (!NonNegativeNumber(row.HasRaidArmor, row.RaidArmor))
        {
            throw Contract("invalid raid armor");
        }
        if (row.Health is { } h)
        {
            foreach (var (present, value) in new[]
            {
                (h.HasSummaryFraction, h.SummaryFraction),
                (h.HasBleedRatePerDay, h.BleedRatePerDay),
                (h.HasPain, h.Pain),
                (h.HasBloodLoss, h.BloodLoss),
                (h.HasHoursUntilDeathFromBloodLoss, h.HoursUntilDeathFromBloodLoss),
            })
            {
                if (!NonNegativeNumber(present, value))
                {
                    throw Contract("invalid combat health number");
                }
            }
            if (h.HasSummaryFraction && h.SummaryFraction > 1)
            {
                throw Contract("invalid combat health fraction");
            }
            if (h.HasBedId && !IsValidId(h.BedId))
            {
                throw Contract("invalid combat bed");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in h.Capacities)
            {
                if (v is null || !IsValidId(v.DefName) || seen.Contains(v.DefName) || !NonNegativeNumber(v.HasLevel, v.Level))
                {
                    throw Contract("invalid combat capacity");
                }
                seen.Add(v.DefName);
                if (v.Unavailable is not null)
                {
                    if (v.HasLevel)
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
                CombatDefName(v.HasDefName, v.DefName);
                if ((v.HasPartDefName && !IsValidId(v.PartDefName))
                    || (v.HasPartIndex && v.PartIndex < 0)
                    || (v.HasTendExpiresInTicks && v.TendExpiresInTicks < 0)
                    || (v.HasNextTendInTicks && v.NextTendInTicks < 0))
                {
                    throw Contract("invalid hediff fields");
                }
                foreach (var (present, value) in new[]
                {
                    (v.HasSeverity, v.Severity),
                    (v.HasTendQuality, v.TendQuality),
                    (v.HasImmunity, v.Immunity),
                    (v.HasSeverityPerDay, v.SeverityPerDay),
                    (v.HasImmunityPerDay, v.ImmunityPerDay),
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
                if (v is null || !IsValidId(v.Id) || seen.Contains(v.Id) || (v.HasRecipe && !IsValidId(v.Recipe)) || (v.HasPartIndex && v.PartIndex < 0))
                {
                    throw Contract("invalid surgery bill");
                }
                seen.Add(v.Id);
            }
            if (h.Snapshot is not null)
            {
                PawnsRef(h.Snapshot, row.Pawn?.Id ?? "", context);
            }
            PawnsIssues(h.Issues, h);
        }
        if (row.Equipment is { } e)
        {
            if ((e.HasPrimaryId && !IsValidId(e.PrimaryId)) || (e.HasCarriedThingId && !IsValidId(e.CarriedThingId)))
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
                    if (!seen.Add(g.Thing.Id))
                    {
                        throw Contract("duplicate gear");
                    }
                    if (g.HasStuff && !IsValidId(g.Stuff))
                    {
                        throw Contract("invalid gear definition");
                    }
                    if (!ValidBiocode(g))
                    {
                        throw Contract("invalid gear biocode");
                    }
                    if ((g.HasHitPoints && g.HitPoints < 0) || (g.HasMaxHitPoints && g.MaxHitPoints <= 0))
                    {
                        throw Contract("invalid gear hit points");
                    }
                    foreach (var (present, value) in new[]
                    {
                        (g.HasConditionFraction, g.ConditionFraction),
                        (g.HasArmorSharp, g.ArmorSharp),
                        (g.HasArmorBlunt, g.ArmorBlunt),
                        (g.HasInsulationCold, g.InsulationCold),
                        (g.HasInsulationHeat, g.InsulationHeat),
                    })
                    {
                        if (!FiniteNumber(present, value))
                        {
                            throw Contract("invalid gear number");
                        }
                    }
                }
            }
            if (e.HasArmed && !e.Armed && e.HasPrimaryId)
            {
                throw Contract("unarmed pawn has primary");
            }
            if (e.HasInventoryItemCount && e.InventoryItemCount < (ulong)e.InventoryWeapons.Count)
            {
                throw Contract("inventory count mismatch");
            }
            PawnsIssues(e.Issues, e);
        }
        if (row.Biography is { } b)
        {
            if (!NonNegativeNumber(b.HasBiologicalAgeYears, b.BiologicalAgeYears)
                || !NonNegativeNumber(b.HasChronologicalAgeYears, b.ChronologicalAgeYears)
                || !PresentationText(b.HasTitle, b.Title, 16384)
                || !PresentationText(b.HasTitleSource, b.TitleSource, 16384))
            {
                throw Contract("invalid biography");
            }
            if (b.HasChildhoodDefName)
            {
                CombatDefName(true, b.ChildhoodDefName);
            }
            if (b.HasAdulthoodDefName)
            {
                CombatDefName(true, b.AdulthoodDefName);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in b.Skills)
            {
                if (v is null)
                {
                    throw Contract("missing skill");
                }
                CombatDefName(v.HasDefName, v.DefName);
                if (!seen.Add(v.DefName)
                    || (v.HasLevel && v.Level < 0)
                    || !NonNegativeNumber(v.HasStoredLevel, v.StoredLevel)
                    || (v.HasPassion && PassionName(v.Passion).Length == 0))
                {
                    throw Contract("invalid skill");
                }
            }

            // Trait degrees are signed native values; hediffs and traits can repeat.
            foreach (var v in b.Traits)
            {
                if (v is null || !IsValidId(v.DefName))
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
                if (entry is null || !IsValidId(entry.DefName) || trainables.Contains(entry.DefName) || !PresentationText(entry.HasReason, entry.Reason, 4096))
                {
                    throw Contract("invalid animal training");
                }
                trainables.Add(entry.DefName);
            }
            if ((a.HasPenId && (!IsValidId(a.PenId) || (a.HasContained && !a.Contained)))
                || (a.HasAllowedAreaId && a.AllowedAreaId.Length != 0 && !IsValidId(a.AllowedAreaId)))
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
        if (UnknownFields.Present(v) || !IsValidId(v.Token) || !v.HasChild || !v.HasSlave || !v.HasIncapableOfViolence || !v.HasDrafted)
        {
            throw Contract("invalid apparel policy census");
        }
        if (!IsValidId(v.Name)
            || !v.HasMinHitPoints
            || !v.HasMaxHitPoints
            || float.IsNaN(v.MinHitPoints)
            || float.IsNaN(v.MaxHitPoints)
            || v.MinHitPoints < 0
            || v.MaxHitPoints > 1
            || v.MinHitPoints > v.MaxHitPoints
            || !v.HasMinQuality
            || !v.HasMaxQuality
            || v.MinQuality < 0
            || v.MaxQuality > 6
            || v.MinQuality > v.MaxQuality
            || !v.HasExcludesTainted)
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
                || !IsValidId(w.DefName)
                || seen.Contains(w.DefName)
                || !w.HasPriority
                || w.Priority < 0
                || w.Priority > DomainConstants.MaxPriority
                || !w.HasDisabled)
            {
                throw Contract("invalid apparel work role");
            }
            seen.Add(w.DefName);
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in v.Skills)
        {
            if (s is null || !IsValidId(s.DefName) || seen.Contains(s.DefName) || !s.HasLevel || s.Level < 0 || s.Level > 20 || !s.HasPassion || !s.HasDisabled)
            {
                throw Contract("invalid apparel skill census");
            }
            seen.Add(s.DefName);
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
                    || !row.HasDefName
                    || !IsValidId(row.DefName)
                    || !row.HasStuff
                    || (row.Stuff.Length != 0 && !IsValidId(row.Stuff))
                    || !row.HasQuality
                    || row.Quality < 0
                    || row.Quality > 6
                    || !row.HasHpBand
                    || row.HpBand < 5
                    || row.HpBand > 9
                    || !row.HasCount
                    || row.Count <= 0)
                {
                    throw Contract("invalid apparel storage row");
                }
                if (!seen.Add((row.DefName, row.Stuff, row.Quality, row.HpBand)))
                {
                    throw Contract("duplicate apparel storage row");
                }
            }
        }
        if (v.OutdoorTemperatureByTwelfthC.Count != 0 || v.HasCurrentTwelfth || v.HasTicksToNextTwelfth || v.ActiveWeather is not null)
        {
            if (v.OutdoorTemperatureByTwelfthC.Count != 12
                || !v.HasCurrentTwelfth
                || v.CurrentTwelfth >= 12
                || !v.HasTicksToNextTwelfth
                || v.TicksToNextTwelfth <= 0
                || v.TicksToNextTwelfth > 300000)
            {
                throw Contract("invalid gear seasonal curve");
            }
            if (v.OutdoorTemperatureByTwelfthC.Any(n => !float.IsFinite(n)))
            {
                throw Contract("invalid gear seasonal temperature");
            }
            if (v.ActiveWeather is { } w)
            {
                if (!w.HasDefName
                    || (w.DefName != "ColdSnap" && w.DefName != "HeatWave")
                    || !w.HasRemainingTicks
                    || w.RemainingTicks < -1
                    || !w.HasTemperatureOffsetC
                    || !float.IsFinite(w.TemperatureOffsetC)
                    || (w.DefName == "ColdSnap" && w.TemperatureOffsetC > 0)
                    || (w.DefName == "HeatWave" && w.TemperatureOffsetC < 0))
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
            var id = p.Pawn.Id;
            if (!people.Add(id))
            {
                throw Contract("duplicate gear pawn");
            }
            PawnsRef(p.Snapshot, id, context);
            ValidateApparelPolicy(p.ApparelPolicy);
            ValidateGearWearer(p);
            if (!PresentationText(p.HasBlocker, p.Blocker, 4096) || (p.HasBlocker && p.Blocker.Length == 0) || (p.HasBlocker && p.Candidates.Count > 0))
            {
                throw Contract("invalid blocked gear loadout");
            }
            if (!FiniteNumber(p.HasComfortableMinC, p.ComfortableMinC)
                || !FiniteNumber(p.HasComfortableMaxC, p.ComfortableMaxC)
                || (p.HasComfortableMinC && p.HasComfortableMaxC && p.ComfortableMinC > p.ComfortableMaxC))
            {
                throw Contract("invalid gear temperature range");
            }
            CombatDetails(new PawnState { Equipment = p.Equipment }, context);
            var candidates = new List<GearItem>();
            foreach (var candidate in p.Candidates)
            {
                if (candidate is null
                    || candidate.Item is null
                    || !candidate.HasGain
                    || !NonNegativeNumber(candidate.HasGain, candidate.Gain)
                    || candidate.Gain <= 0
                    || candidate.HasBlocker
                    || candidate.HasReason)
                {
                    throw Contract("invalid eligible gear candidate");
                }
                candidates.Add(candidate.Item);
            }
            var carried = new PawnEquipment();
            carried.Equipped.AddRange(candidates);
            CombatDetails(new PawnState { Equipment = carried }, context);
            if (p.LoadoutModel is null || !p.HasComfortableMinC || !p.HasComfortableMaxC)
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
        if (!p.HasGender || !Enum.IsDefined(p.Gender) || !CombatIdsValid(p.BodyPartGroups))
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
        if (m.Traits.Any(t => t is null || !IsValidId(t.DefName)))
        {
            throw Contract("invalid gear model trait");
        }
        foreach (var list in new[] { m.Worn, m.Options })
        {
            foreach (var x in list)
            {
                if (x is null || !IsValidId(x.Id) || !IsValidId(x.DefName) || (x.HasStuff && !IsValidId(x.Stuff)) || !x.HasQuality || !x.HasCondition)
                {
                    throw Contract("invalid gear model option");
                }
                if (x.Source is not ("worn" or "loose" or "stored" or "bill"))
                {
                    throw Contract("invalid gear model option source");
                }
                if (!FiniteNumber(x.HasCondition, x.Condition))
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
        foreach (var row in v.CombinedFoodSupply.Consumers)
        {
            consumers.Add(row.PawnId);
            rates[row.PawnId] = OptionalDouble(row.HasNutritionPerDay, row.NutritionPerDay);
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
                var id = row.PawnId;
                if (!consumers.Contains(id) || !partition.Add(id))
                {
                    throw Contract("inconsistent combined consumers");
                }
                if (rates[id] is { } rate && row.HasNutritionPerDay && rate != row.NutritionPerDay)
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
                || zones.Contains(row.Zone!.Id)
                || (row.HasCrop && !IsValidId(row.Crop))
                || (row.HasProduct && !IsValidId(row.Product)))
            {
                throw Contract("invalid crop forecast");
            }
            zones.Add(row.Zone.Id);
            PawnsIssues(row.Issues, row);
            if (!NonNegativeNumber(row.HasSowWork, row.SowWork)
                || !NonNegativeNumber(row.HasHarvestWork, row.HarvestWork)
                || !NonNegativeNumber(row.HasStandingYield, row.StandingYield))
            {
                throw Contract("invalid crop quantity");
            }
        }
        var patients = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Patients)
        {
            if (row is null || !IsValidId(row.PawnId) || !patients.Add(row.PawnId))
            {
                throw Contract("invalid patient forecast");
            }
            PawnsIssues(row.Issues, row);
            if (!NonNegativeNumber(row.HasBleedRatePerDay, row.BleedRatePerDay) || !NonNegativeNumber(row.HasHoursUntilDeathFromBloodLoss, row.HoursUntilDeathFromBloodLoss))
            {
                throw Contract("invalid medical quantity");
            }
            if (!FiniteNumber(row.HasMood, row.Mood)
                || !FiniteNumber(row.HasMoodTarget, row.MoodTarget)
                || !FiniteNumber(row.HasMinorBreakThreshold, row.MinorBreakThreshold)
                || !FiniteNumber(row.HasMajorBreakThreshold, row.MajorBreakThreshold)
                || !FiniteNumber(row.HasExtremeBreakThreshold, row.ExtremeBreakThreshold))
            {
                throw Contract("invalid mood quantity");
            }
        }
        if (human is not null && human.Consumers.Any(row => !patients.Contains(row.PawnId)))
        {
            throw Contract("human patient forecast missing");
        }
        if (v.AnimalIds.Any(patients.Contains))
        {
            throw Contract("animal in human patient census");
        }
    }
}
