using System.Text;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using static RimGovernor.Brain.Domain.Fact;
using PolicyMentalState = RimGovernor.Brain.Policy.MentalState;

namespace RimGovernor.Brain.Host;

// Go pawns.go (pawnsSnapshotSelected and the row checks under it), combat_pawns.go (validateDetailedPawnSnapshot,
// combatDetails), work_pawns.go (validateSettings), mood.go, body_stats.go, policies.go (validatePolicyInputs),
// biotech.go (validatePawnBiotech), anomaly.go (validatePawnAnomaly, validateStudyState), mental_state.go and
// catalog_rows.go (catalogNumbers): the validators every pawn-list read runs on its reply. The shared ones that
// already exist in Bridge.Contracts (ValidateContext, ValidateUnavailable, PawnsIssues, ValidRef, CombatNumber)
// are reused; combatDetails and pawnsRef are private there, so they are repeated here.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>pawnDetails</c>: the PawnDetails families one read asks for. The reply validator refuses any family the request did not select.</summary>
    internal readonly record struct PawnDetailSelection(bool Combat = false, bool Work = false, bool Care = false, bool Schedule = false, bool Social = false, bool Tend = false);

    /// <summary>The checks a pawn-list reply goes through, one method per Go validator.</summary>
    internal static class PawnReadChecks
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>Go <c>presentationText</c>: an absent text, or valid UTF-8 of at most <paramref name="limit"/> bytes.</summary>
        internal static bool PresentationText(string? text, int limit)
        {
            if (text is null)
            {
                return true;
            }
            try
            {
                return StrictUtf8.GetByteCount(text) <= limit;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Go <c>catalogNumbers</c>: every stated number is finite.</summary>
        internal static void CatalogNumbers(string kind, params double?[] values)
        {
            foreach (var v in values)
            {
                if (v is { } n && !double.IsFinite(n))
                {
                    throw Contract($"nonfinite {kind} number");
                }
            }
        }

        /// <summary>Go <c>combatIDs</c>: every id valid and none repeated.</summary>
        private static void CombatIds(IEnumerable<string> values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in values)
            {
                if (!IsValidId(id) || !seen.Add(id))
                {
                    throw Contract("invalid duplicate combat detail ID");
                }
            }
        }

        /// <summary>Go <c>combatDefName</c>: a definition name that is stated and valid.</summary>
        private static void CombatDefName(string? value)
        {
            if (value is null)
            {
                throw Contract("combat definition missing");
            }
            if (!IsValidId(value))
            {
                throw Contract("combat definition invalid");
            }
        }

        /// <summary>Go <c>validBiocode</c>.</summary>
        private static bool ValidBiocode(PbObservations.GearItem g) =>
            g.BiocodedTo is null || (IsValidId(g.BiocodedTo) && (g.Biocoded is null || g.Biocoded.GetValueOrDefault()));

        /// <summary>Go <c>validateDetailedPawnSnapshot</c>: the snapshot against the exact ids asked for.</summary>
        internal static void ValidateDetailedPawnSnapshot(PbObservations.PawnSnapshot? snapshot, PbCommon.Identity? identity, IReadOnlyList<string> ids, PawnDetailSelection want)
        {
            ValidateIdentity(identity);
            if (ids.Count < 1 || ids.Count > 256)
            {
                throw Contract("pawn IDs outside 1..256");
            }
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!IsValidId(id) || !requested.Add(id))
                {
                    throw Contract("invalid or duplicate requested pawn");
                }
            }
            BuildingUnknown(snapshot);
            PawnsSnapshotSelected(snapshot, identity, requested, want);
        }

        /// <summary>Go <c>pawnsSnapshotSelected</c>.</summary>
        internal static void PawnsSnapshotSelected(PbObservations.PawnSnapshot? v, PbCommon.Identity? id, ISet<string> requested, PawnDetailSelection want)
        {
            var combat = want.Combat;
            var work = want.Work;
            var care = want.Care;
            var schedule = want.Schedule;
            var social = want.Social;
            if (v is null)
            {
                throw Contract("pawn snapshot missing");
            }
            ValidateContext(v.Context);
            var context = v.Context!;
            if (!SameIdentity(context.Identity, id))
            {
                throw Contract("pawn world mismatch");
            }

            // ListPawns counts matched query rows; filtered counts excluded source rows.
            // Those excluded rows are not unreadable members of the exact-ID result.
            if (v.Pawns.Count > requested.Count)
            {
                throw Contract("incomplete pawn query");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in v.Pawns)
            {
                if (row is null || row.Pawn is null || !requested.Contains(row.Pawn.Id ?? "") || !seen.Add(row.Pawn.Id ?? ""))
                {
                    throw Contract("unrequested or duplicate pawn");
                }
                PawnsEntity(row.Pawn, context);
                if (row.PawnSnapshot is not null)
                {
                    PawnsRef(row.PawnSnapshot, row.Pawn.Id ?? "", context);
                }
                if ((!work && row.Needs is not null)
                    || (!combat && (row.Health is not null || row.Equipment is not null || row.Biography is not null || row.AnimalState is not null))
                    || (!work && !care && !schedule && row.Settings is not null)
                    || (!social && row.Social is not null)
                    || (!want.Tend && row.TendDoctor is not null))
                {
                    throw Contract("unrequested pawn detail");
                }
                if (want.Tend)
                {
                    PawnsTendDoctor(row, requested);
                }
                if (row.Social is not null)
                {
                    PawnsSocial(row.Social);
                }
                if (combat)
                {
                    CombatDetails(row, context);
                }
                if (row.Settings is not null)
                {
                    ValidateSettings(row.Settings, work, care, schedule);
                }
                if (work && row.Needs is not null)
                {
                    ValidateMoodNeeds(row.Needs);
                }
                foreach (var value in new[] { row.KindDefName, row.Faction?.Id, row.MentalState, row.OwnedBed?.Id, row.LordJobClass, row.LordToilClass })
                {
                    if (value is not null)
                    {
                        ValidId(value);
                    }
                }
                _ = EmergencyDecode.PawnMentalState(row);
                if (!PresentationText(row.HostileReason, 4096))
                {
                    throw Contract("invalid pawn hostile reason");
                }
                if (row.NearestColonistDistance is { } distance && (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0))
                {
                    throw Contract("invalid pawn number");
                }
                if (row.NearestColonist is not null && !ValidRef(row.NearestColonist))
                {
                    throw Contract("invalid nearest colonist");
                }
                ValidatePawnBiotech(row.Biotech);
                ValidatePawnAnomaly(row.Anomaly);
                PawnsIssues(row.Issues, row);
                if (row.Job is not null)
                {
                    PawnsJob(row.Job);
                }
            }
        }

        /// <summary>Go <c>pawnsEntity</c>.</summary>
        internal static void PawnsEntity(PbObservations.EntityRef? v, PbCommon.ObservationContext context)
        {
            if (v is null)
            {
                throw Contract("pawn entity missing");
            }
            ValidId(v.Id ?? "");
            if (v.DefName is not null)
            {
                ValidId(v.DefName);
            }
            if (!PresentationText(v.Label, 16384))
            {
                throw Contract("invalid pawn label");
            }
            if (v.MapId is not null && v.MapId.GetValueOrDefault() != (context.Identity?.MapId ?? 0))
            {
                throw Contract("pawn entity map mismatch");
            }
            if (v.Position is not null && (v.Position.X is null || v.Position.Z is null || v.Position.X.GetValueOrDefault() < 0 || v.Position.Z.GetValueOrDefault() < 0))
            {
                throw Contract("invalid pawn position");
            }
        }

        /// <summary>Go <c>pawnsRef</c>: a CAS token that names <paramref name="id"/> and was read under, not after, <paramref name="context"/>.</summary>
        internal static void PawnsRef(PbObservations.SnapshotRef v, string id, PbCommon.ObservationContext context)
        {
            ValidateContext(v.Context);
            if ((v.EntityId ?? "") != id || !SameIdentity(v.Context?.Identity, context.Identity) || (v.Context?.Tick).GetValueOrDefault() > context.Tick.GetValueOrDefault())
            {
                throw Contract("pawn CAS context or entity mismatch");
            }
            ValidId(v.Token ?? "");
        }

        /// <summary>Go <c>pawnsSocial</c>: each thought row names a def with finite offsets; relations are only bounded.</summary>
        internal static void PawnsSocial(PbObservations.PawnSocial v)
        {
            foreach (var rows in new[] { v.Memories, v.Situational })
            {
                foreach (var t in rows)
                {
                    if (t is null || !IsValidId(t.DefName ?? ""))
                    {
                        throw Contract("invalid pawn thought");
                    }
                    foreach (var value in new[] { t.MoodOffsetEach, t.MoodOffsetTotal })
                    {
                        if (value is { } n && (double.IsNaN(n) || double.IsInfinity(n)))
                        {
                            throw Contract("invalid thought offset");
                        }
                    }
                }
            }
            PawnsIssues(v.Issues, v);
        }

        /// <summary>Go <c>pawnsTendDoctor</c>: the doctor-side tend gates; every reachable id is another requested pawn.</summary>
        internal static void PawnsTendDoctor(PbObservations.PawnState row, ISet<string> requested)
        {
            var tend = row.TendDoctor;
            if (tend is null)
            {
                return;
            }
            if (tend.ReachSkipped.GetValueOrDefault() && tend.ReachablePawnIds.Count != 0)
            {
                throw Contract("skipped tend reachability lists pawns");
            }
            if (tend.ReachablePawnIds.Count > requested.Count)
            {
                throw Contract("too many reachable pawns");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in tend.ReachablePawnIds)
            {
                ValidId(id);
                if (!requested.Contains(id) || id == (row.Pawn?.Id ?? "") || !seen.Add(id))
                {
                    throw Contract("unrequested, self or duplicate reachable pawn");
                }
            }
            if (tend.MissingCapacity is not null)
            {
                ValidId(tend.MissingCapacity);
                if (tend.CapacitiesOk is null || tend.CapacitiesOk.GetValueOrDefault())
                {
                    throw Contract("missing tend capacity contradicts capacities_ok");
                }
            }
            PawnsIssues(tend.Issues, tend);
        }

        /// <summary>Go <c>pawnsJob</c>.</summary>
        internal static void PawnsJob(PbObservations.JobEvidence v)
        {
            foreach (var id in new[] { v.DefName, v.LoadId })
            {
                if (id is not null)
                {
                    ValidId(id);
                }
            }
            if (!PresentationText(v.Report, 16384) || (v.NativePriority is { } priority && (double.IsNaN(priority) || double.IsInfinity(priority))))
            {
                throw Contract("invalid pawn job");
            }
            PawnsIssues(v.Issues, v);
            if (v.TargetA is { } target)
            {
                switch (target.Target)
                {
                    case PbObservations.TargetRefTarget.EntityCase { Value: var entity }:
                        if (!ValidRef(entity))
                        {
                            throw Contract("invalid pawn job target");
                        }
                        break;
                    case PbObservations.TargetRefTarget.CellCase { Value: var cell }:
                        if (cell is null || cell.X is null || cell.Z is null || cell.X.GetValueOrDefault() < 0 || cell.Z.GetValueOrDefault() < 0)
                        {
                            throw Contract("invalid pawn job cell");
                        }
                        break;
                    case PbObservations.TargetRefTarget.UnavailableCase { Value: var unavailable }:
                        ValidateUnavailable(unavailable);
                        return;
                    default:
                        throw Contract("pawn job target missing");
                }
            }
        }

        /// <summary>Go <c>combatDetails</c>: the health, equipment, biography and animal facts of a pawn row.</summary>
        internal static void CombatDetails(PbObservations.PawnState row, PbCommon.ObservationContext context)
        {
            if (!CombatNumber(row.RaidArmor, true))
            {
                throw Contract("invalid raid armor");
            }
            if (row.Health is { } h)
            {
                foreach (var value in new[] { h.SummaryFraction, h.BleedRatePerDay, h.Pain, h.BloodLoss, h.HoursUntilDeathFromBloodLoss })
                {
                    if (!CombatNumber(value, true))
                    {
                        throw Contract("invalid combat health number");
                    }
                }
                if (h.SummaryFraction is not null && h.SummaryFraction.GetValueOrDefault() > 1)
                {
                    throw Contract("invalid combat health fraction");
                }
                if (h.BedId is not null && !IsValidId(h.BedId))
                {
                    throw Contract("invalid combat bed");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in h.Capacities)
                {
                    if (v is null || !IsValidId(v.DefName ?? "") || seen.Contains(v.DefName ?? "") || !CombatNumber(v.Level, true))
                    {
                        throw Contract("invalid combat capacity");
                    }
                    seen.Add(v.DefName ?? "");
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
                    CombatDefName(v.DefName);
                    if ((v.PartDefName is not null && !IsValidId(v.PartDefName))
                        || (v.PartIndex is not null && v.PartIndex.GetValueOrDefault() < 0)
                        || (v.TendExpiresInTicks is not null && v.TendExpiresInTicks.GetValueOrDefault() < 0)
                        || (v.NextTendInTicks is not null && v.NextTendInTicks.GetValueOrDefault() < 0))
                    {
                        throw Contract("invalid hediff fields");
                    }
                    foreach (var n in new[] { v.Severity, v.TendQuality, v.Immunity, v.SeverityPerDay, v.ImmunityPerDay })
                    {
                        if (!CombatNumber(n, false))
                        {
                            throw Contract("nonfinite hediff");
                        }
                    }
                }
                seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in h.SurgeryBills)
                {
                    if (v is null || !IsValidId(v.Id ?? "") || seen.Contains(v.Id ?? "") || (v.Recipe is not null && !IsValidId(v.Recipe)) || (v.PartIndex is not null && v.PartIndex.GetValueOrDefault() < 0))
                    {
                        throw Contract("invalid surgery bill");
                    }
                    seen.Add(v.Id ?? "");
                }
                if (h.Snapshot is not null)
                {
                    PawnsRef(h.Snapshot, row.Pawn?.Id ?? "", context);
                }
                PawnsIssues(h.Issues, h);
            }
            if (row.Equipment is { } e)
            {
                if ((e.PrimaryId is not null && !IsValidId(e.PrimaryId)) || (e.CarriedThingId is not null && !IsValidId(e.CarriedThingId)))
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
                        if (!seen.Add(g.Thing.Id ?? ""))
                        {
                            throw Contract("duplicate gear");
                        }
                        if (g.Stuff is not null && !IsValidId(g.Stuff))
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
                        foreach (var n in new[] { g.ConditionFraction, g.ArmorSharp, g.ArmorBlunt, g.InsulationCold, g.InsulationHeat })
                        {
                            if (!CombatNumber(n, false))
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
                if (!CombatNumber(b.BiologicalAgeYears, true)
                    || !CombatNumber(b.ChronologicalAgeYears, true)
                    || !PresentationText(b.Title, 16384)
                    || !PresentationText(b.TitleSource, 16384))
                {
                    throw Contract("invalid biography");
                }
                foreach (var v in new[] { b.ChildhoodDefName, b.AdulthoodDefName })
                {
                    if (v is not null)
                    {
                        CombatDefName(v);
                    }
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in b.Skills)
                {
                    if (v is null)
                    {
                        throw Contract("missing skill");
                    }
                    CombatDefName(v.DefName);
                    if (!seen.Add(v.DefName ?? "")
                        || (v.Level is not null && v.Level.GetValueOrDefault() < 0)
                        || !CombatNumber(v.StoredLevel, true)
                        || (v.Passion is not null && PassionName(v.Passion.GetValueOrDefault()).Length == 0))
                    {
                        throw Contract("invalid skill");
                    }
                }

                // Trait degrees are signed native values; hediffs and traits can repeat.
                foreach (var v in b.Traits)
                {
                    if (v is null || !IsValidId(v.DefName ?? ""))
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
                    if (entry is null || !IsValidId(entry.DefName ?? "") || trainables.Contains(entry.DefName ?? "") || !PresentationText(entry.Reason, 4096))
                    {
                        throw Contract("invalid animal training");
                    }
                    trainables.Add(entry.DefName ?? "");
                }
                if ((a.PenId is not null && (!IsValidId(a.PenId) || (a.Contained is not null && !a.Contained.GetValueOrDefault())))
                    || (a.AllowedAreaId is not null && a.AllowedAreaId.Length != 0 && !IsValidId(a.AllowedAreaId)))
                {
                    throw Contract("invalid animal herd facts");
                }
                PawnsIssues(a.Issues, a);
            }
        }

        /// <summary>
        /// Go <c>validateSettings</c>: PawnSettings carries only the fields the request asked for. Work priorities and the
        /// allowed area belong to work, care policy to care, timetable slots to schedule.
        /// </summary>
        internal static void ValidateSettings(PbObservations.PawnSettings s, bool work, bool care, bool schedule)
        {
            var allowed = new PbObservations.PawnSettings { Issues = s.Issues };
            if (work)
            {
                allowed = allowed with
                {
                    FoodRestriction = s.FoodRestriction,
                    PolicyInputs = s.PolicyInputs,
                    EatTrigger = s.EatTrigger,
                    Body = s.Body,
                    Work = s.Work,
                    WorkApplies = s.WorkApplies,
                    ManualWorkPriorities = s.ManualWorkPriorities,
                    AllowedAreaId = s.AllowedAreaId,
                };
            }
            if (care)
            {
                allowed = allowed with { MedicalCare = s.MedicalCare, SelfTend = s.SelfTend, HostilityResponse = s.HostilityResponse };
            }
            if (schedule)
            {
                allowed = allowed with { Schedule = s.Schedule };
            }
            if (s != allowed || s.Schedule.Count > 24)
            {
                throw Contract("unrequested settings detail");
            }
            ValidateBodyStats(s.Body);
            ValidatePolicyInputs(s.PolicyInputs);
            if (s.EatTrigger is { } eat && (float.IsNaN(eat) || eat < 0 || eat > 1))
            {
                throw Contract("invalid eat trigger");
            }
            if (s.FoodRestriction is { } food)
            {
                if (!IsValidId(food.PolicyId ?? ""))
                {
                    throw Contract("invalid food policy identity");
                }
                var seenDefs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var def in food.AllowedDefs)
                {
                    if (!IsValidId(def) || !seenDefs.Add(def))
                    {
                        throw Contract("invalid food definition");
                    }
                }
            }
            PawnsIssues(s.Issues, s);
            if (work)
            {
                if (s.WorkApplies is not null && !s.WorkApplies.GetValueOrDefault() && s.Work.Count > 0)
                {
                    throw Contract("inapplicable work contains priorities");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var w in s.Work)
                {
                    if (w is null
                        || !IsValidId(w.DefName ?? "")
                        || seen.Contains(w.DefName ?? "")
                        || (w.Priority is not null && (w.Priority.GetValueOrDefault() < 0 || w.Priority.GetValueOrDefault() > DomainConstants.MaxPriority)))
                    {
                        throw Contract("invalid or duplicate work priority");
                    }
                    seen.Add(w.DefName ?? "");
                    if (s.ManualWorkPriorities is not null && !s.ManualWorkPriorities.GetValueOrDefault() && w.Priority is not null && w.Priority.GetValueOrDefault() != 0 && w.Priority.GetValueOrDefault() != 3)
                    {
                        throw Contract("invalid effective checkbox priority");
                    }
                }
            }
            if (work && s.AllowedAreaId is not null)
            {
                ValidId(s.AllowedAreaId);
            }
            if (care && s.HostilityResponse is not null && HostilityName(s.HostilityResponse.GetValueOrDefault()) is null)
            {
                throw Contract("invalid hostility response");
            }
            if (care && s.MedicalCare is not null && MedicalCareName(s.MedicalCare.GetValueOrDefault()).Length == 0)
            {
                throw Contract("invalid medical care");
            }
            if (schedule)
            {
                var seenHours = new HashSet<uint>();
                foreach (var slot in s.Schedule)
                {
                    if (slot is null || slot.Hour is null || slot.Hour.GetValueOrDefault() > 23 || !seenHours.Add(slot.Hour.GetValueOrDefault()))
                    {
                        throw Contract("invalid or duplicate timetable slot");
                    }
                    if (slot.AssignmentDefName is not null)
                    {
                        ValidId(slot.AssignmentDefName);
                    }
                }
            }
        }

        /// <summary>Go <c>validateBodyStats</c>: finite temperatures; finite, non-negative speed, capacity, resistance, severity and capacity levels.</summary>
        internal static void ValidateBodyStats(PbObservations.PawnBodyStats? b)
        {
            if (b is null)
            {
                return;
            }
            foreach (var v in new[] { b.ComfortableMinC, b.ComfortableMaxC, b.CellTemperatureC })
            {
                if (v is { } n && (double.IsNaN(n) || double.IsInfinity(n)))
                {
                    throw Contract("invalid body temperature");
                }
            }
            foreach (var v in new[] { b.MoveSpeed, b.CarryCapacity, b.ToxicEnvironmentResistance, b.Hypothermia, b.Heatstroke, b.Frostbite, b.ToxicBuildup, b.Moving, b.Manipulation, b.Consciousness })
            {
                if (v is { } n && (double.IsNaN(n) || double.IsInfinity(n) || n < 0))
                {
                    throw Contract("invalid body stat");
                }
            }
        }

        /// <summary>Go <c>validatePolicyInputs</c>: the per-pawn policy inputs row.</summary>
        internal static void ValidatePolicyInputs(PbObservations.PawnPolicyInputs? p)
        {
            if (p is null)
            {
                return;
            }
            foreach (var id in new[] { p.OutfitPolicyId, p.DrugPolicyId, p.ReadingPolicyId, p.RoyalTitle, p.IdeoId, p.IdeoRole, p.GuestStatus, p.PrisonerInteraction, p.SlaveInteraction })
            {
                if (id is not null && !IsValidId(id))
                {
                    throw Contract("invalid policy input identifier");
                }
            }
            if (!CombatNumber(p.MedicalTendQuality, true))
            {
                throw Contract("invalid medical tend quality");
            }
            if (p.IdeoCertainty is { } c && (p.IdeoId is null || !CombatNumber(c, true) || c > 1))
            {
                throw Contract("invalid ideoligion certainty");
            }
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in p.InventoryStock)
            {
                if (s is null || !IsValidId(s.Group ?? "") || groups.Contains(s.Group ?? "") || !IsValidId(s.ThingDef ?? "") || s.Count is null || s.Count.GetValueOrDefault() < 0)
                {
                    throw Contract("invalid inventory stock setting");
                }
                groups.Add(s.Group ?? "");
            }
            var chemicals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chemical in p.Chemicals)
            {
                if (chemical is null
                    || !IsValidId(chemical.Chemical ?? "")
                    || chemicals.Contains(chemical.Chemical ?? "")
                    || (chemical.Addiction is null && chemical.Tolerance is null)
                    || (chemical.Addiction is null) != (chemical.Withdrawal is null)
                    || !CombatNumber(chemical.Addiction, true)
                    || !CombatNumber(chemical.Tolerance, true))
                {
                    throw Contract("invalid chemical state");
                }
                chemicals.Add(chemical.Chemical ?? "");
            }
            foreach (var list in new[] { p.DependencyChemicals, p.Precepts, p.PreceptApparel })
            {
                foreach (var id in list)
                {
                    if (!IsValidId(id))
                    {
                        throw Contract("invalid policy input definition");
                    }
                }
            }
            foreach (var r in p.RoleApparel)
            {
                if (r is null)
                {
                    throw Contract("invalid apparel requirement");
                }
                foreach (var list in new[] { r.BodyPartGroups, r.RequiredDefs, r.RequiredTags, r.AllowedTags })
                {
                    foreach (var id in list)
                    {
                        if (!IsValidId(id))
                        {
                            throw Contract("invalid apparel requirement");
                        }
                    }
                }
            }
        }

        /// <summary>Go <c>validateMoodNeeds</c>.</summary>
        internal static void ValidateMoodNeeds(PbObservations.PawnNeeds v)
        {
            PawnsIssues(v.Issues, v);
            foreach (var n in new[] { v.Food, v.Rest, v.Mood, v.Joy, v.BreakThresholdMinor, v.BreakThresholdMajor, v.BreakThresholdExtreme })
            {
                if (!CombatNumber(n, false))
                {
                    throw Contract("nonfinite pawn need");
                }
            }
            if ((v.HungerCategory is { } hunger && !Enum.IsDefined(hunger)) || (v.BreakRisk is { } risk && !Enum.IsDefined(risk)))
            {
                throw Contract("invalid pawn need category");
            }

            // Psyfocus: all three or none (no Royalty, no psylink).
            if ((v.Psyfocus is null) != (v.PsyfocusTarget is null) || (v.Psyfocus is null) != (v.PsylinkLevel is null))
            {
                throw Contract("partial psyfocus facts");
            }
            if (v.Psyfocus is not null && (!UnitFraction(v.Psyfocus.GetValueOrDefault()) || !UnitFraction(v.PsyfocusTarget.GetValueOrDefault()) || v.PsylinkLevel.GetValueOrDefault() < 1))
            {
                throw Contract("invalid psyfocus facts");
            }
        }

        private static bool UnitFraction(double f) => f >= 0 && f <= 1;

        /// <summary>Go <c>validateStudyState</c>: a CompStudiable block of a pawn or building row.</summary>
        internal static void ValidateStudyState(PbObservations.StudyState? s)
        {
            if (s is null)
            {
                return;
            }
            CatalogNumbers("anomaly study", s.ProgressPercent, s.StudyPoints, s.AnomalyKnowledgeGained, s.AnomalyKnowledge);
            if (s.ProgressPercent is not null && (s.ProgressPercent.GetValueOrDefault() < 0 || s.ProgressPercent.GetValueOrDefault() > 1))
            {
                throw Contract("anomaly study progress outside 0..1");
            }
            if ((s.StudyPoints is not null && s.StudyPoints.GetValueOrDefault() < 0)
                || (s.AnomalyKnowledgeGained is not null && s.AnomalyKnowledgeGained.GetValueOrDefault() < 0)
                || (s.AnomalyKnowledge is not null && s.AnomalyKnowledge.GetValueOrDefault() < 0)
                || (s.StudyInteractions is not null && s.StudyInteractions.GetValueOrDefault() < 0))
            {
                throw Contract("negative anomaly study number");
            }
            if (s.KnowledgeCategory is not null && !IsValidId(s.KnowledgeCategory))
            {
                throw Contract("invalid anomaly study knowledge category");
            }
        }

        /// <summary>Go <c>validatePawnAnomaly</c>: a pawn row's Anomaly block.</summary>
        internal static void ValidatePawnAnomaly(PbObservations.PawnAnomaly? a)
        {
            if (a is null)
            {
                return;
            }
            CatalogNumbers("anomaly pawn", a.MinContainmentStrength);
            if (a.Held is { } h)
            {
                CatalogNumbers("anomaly held", h.BioferritePerDay);
                if (h.Platform is not null && !ValidRef(h.Platform))
                {
                    throw Contract("invalid anomaly holding platform");
                }
                if (h.Mode is { } mode && (mode == PbObservations.EntityContainmentModeKind.Unspecified || !Enum.IsDefined(mode)))
                {
                    throw Contract("anomaly pawn has an unknown containment mode");
                }
            }
            if (a.Creepjoiner is { } creep)
            {
                foreach (var id in new[] { creep.Form, creep.Benefit })
                {
                    if (id is not null && !IsValidId(id))
                    {
                        throw Contract("invalid creepjoiner def name");
                    }
                }
            }
            ValidateStudyState(a.Study);
            PawnsIssues(a.Issues, a);
        }

        /// <summary>Go <c>validatePawnBiotech</c>: a pawn row's Biotech block.</summary>
        internal static void ValidatePawnBiotech(PbObservations.PawnBiotech? b)
        {
            if (b is null)
            {
                return;
            }
            CatalogNumbers("biotech pawn", b.Learning);
            if (b.Learning is not null && (b.Learning.GetValueOrDefault() < 0 || b.Learning.GetValueOrDefault() > 1))
            {
                throw Contract("biotech learning outside 0..1");
            }
            foreach (var id in new[] { b.LifeStage, b.DevelopmentalStage, b.LearningCategory, b.Xenotype })
            {
                if (id is not null && !IsValidId(id))
                {
                    throw Contract("invalid biotech pawn name");
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in b.Genes)
            {
                if (g is null || !IsValidId(g.DefName ?? "") || seen.Contains(g.DefName ?? "") || g.Xenogene is null || g.Active is null)
                {
                    throw Contract("invalid or duplicate biotech pawn gene");
                }
                seen.Add(g.DefName ?? "");
            }
            if (b.Mechanitor is { } mechanitor)
            {
                foreach (var n in new[] { mechanitor.UsedBandwidth, mechanitor.TotalBandwidth, mechanitor.UsedBandwidthFromGestation, mechanitor.ControlGroups })
                {
                    if (n is < 0)
                    {
                        throw Contract("negative mechanitor number");
                    }
                }
                foreach (var mech in mechanitor.ControlledMechs)
                {
                    if (!ValidRef(mech))
                    {
                        throw Contract("invalid controlled mech");
                    }
                }
            }
            if (b.Mech is { } m)
            {
                if ((m.Overseer is not null && !ValidRef(m.Overseer))
                    || (m.WorkMode is not null && !IsValidId(m.WorkMode))
                    || (m.ControlGroup is not null && m.ControlGroup.GetValueOrDefault() < 0))
                {
                    throw Contract("invalid biotech mech");
                }
                CatalogNumbers("biotech mech", m.Energy, m.RechargeBelow, m.RechargeAbove);
                foreach (var n in new[] { m.Energy, m.RechargeBelow, m.RechargeAbove })
                {
                    if (n is { } value && (value < 0 || value > 1))
                    {
                        throw Contract("mech energy outside 0-1");
                    }
                }
                if (m.RechargeBelow is { } below && m.RechargeAbove is { } above && below > above)
                {
                    throw Contract("mech recharge band inverted");
                }
            }
            if (b.Deathrest is { } d)
            {
                CatalogNumbers("biotech deathrest", d.Level, d.DeathrestPercent);
                foreach (var n in new[] { d.Capacity, d.BoundBuildings })
                {
                    if (n is < 0)
                    {
                        throw Contract("negative deathrest number");
                    }
                }
            }
            foreach (var n in new[] { b.XenogermRegrowTicksLeft, b.XenogermComaTicksLeft })
            {
                if (n is < 0)
                {
                    throw Contract("negative xenogerm ticks left");
                }
            }
            if (b.ExtractableReason is not null && (b.Extractable is null || b.Extractable.GetValueOrDefault() || b.ExtractableReason.Length > 512 || !AsciiOnly(b.ExtractableReason)))
            {
                throw Contract("invalid extractable reason");
            }
            PawnsIssues(b.Issues, b);
        }

        private static bool AsciiOnly(string s) => s.All(c => c < 0x80);
    }
}
