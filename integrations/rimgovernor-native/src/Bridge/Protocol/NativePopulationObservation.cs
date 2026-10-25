using System;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Dedicated per-cycle population census on the official protobuf
    // boundary, the same migration NativeHusbandryOperations/NativePrisonerInteractionOperations
    // already made for the write side. Population-*'s recruit/maintain deficit
    // facts (recruitable, current interaction) live only here -- the generic
    // colony/upkeep census NativeUpkeepFacts populates has no guest section --
    // so this is the one native read the routine planner needs to detect and
    // select a prisoner-interaction write. Single complete page, like
    // ReadStatus/ReadPrisonerInteraction; a paginated population
    // is deferred to whatever candidate search eventually needs one.
    internal static class NativePopulationObservation
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade. A refused request
        // or a population that cannot be read completely throws.
        internal static Obs.PopulationSnapshot HandleReadPopulation(Obs.PopulationRequest request)
        {
            if (!ValidatePopulation(request, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out _, out var failure))
                throw NativeFaults.Refused(failure);
            try { return Population(map, request); }
            catch (Exception error)
            {
                ModLog.Error("observe", "Population read failed: " + error);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Population could not be read completely: " + error.GetType().Name + ": " + error.Message) });
            }
        }

        internal static bool ValidatePopulation(Obs.PopulationRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope is required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        // The population as a frame section: a read that throws fails the frame. Every person is read whole.
        internal static Obs.PopulationSnapshot Population(Map map, Obs.PopulationRequest request)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).ToList();
            var persons = new System.Collections.Generic.List<Obs.PopulationPerson>(people.Count);
            foreach (var p in people)
            {
                var guest = p.guest ?? throw new InvalidOperationException("Humanlike without a guest tracker.");
                var health = p.health?.summaryHealth ?? throw new InvalidOperationException("Humanlike without a health summary.");
                persons.Add(new Obs.PopulationPerson
                {
                    Pawn = PawnId(p),
                    Admitted = p.IsFreeColonist && p.Faction == player,
                    Guest = p.HostFaction == player,
                    Recruitable = guest.Recruitable,
                    IdeoId = p.Ideo == null ? null : NativePawnObservationTools.Id(p.Ideo.GetUniqueLoadID()),
                    // Prospect facts: the prisoners family weighs a prisoner's skills, traits, age and health against
                    // the free colonists'; a hostile's skills rank it as a lance target.
                    WildMan = p.IsWildMan(),
                    Biography = NativePawnDetails.Biography(p),
                    HealthSummary = Number(health.SummaryHealthPercent),
                    // After combat: a luciferium addict is stripped and finished, not captured; every raider is stripped first.
                    LuciferiumAddicted = CombatMirror.HasHediff(p, "LuciferiumAddiction"),
                    WearingApparel = p.apparel?.WornApparel.Count > 0,
                    Custody = Custody(p, player, guest),
                    WillJoinIfRescued = p.mindState.WillJoinColonyIfRescued,
                });
            }
            // Owned-pawn names: the census the unique-name planner reads.
            var names = NativePawnSettings.OwnedNamedPawns().Select(owned => new Obs.OwnedName
            {
                PawnId = PawnId(owned),
                ShortName = NativePawnObservationTools.Text(owned.Name.ToStringShort),
                ThingId = owned.thingIDNumber,
            }).ToList();
            Obs.ColonyIdeology ideology = ModsConfig.IdeologyActive
                ? new Obs.ColonyIdeology.Installed(Find.IdeoManager.classicMode,
                    player.ideos?.PrimaryIdeo is Ideo ideo ? NativePawnObservationTools.Id(ideo.GetUniqueLoadID()) : null)
                : new Obs.ColonyIdeology.NotInstalled();
            return new Obs.PopulationSnapshot { Persons = [.. persons], Ideology = ideology, OwnedNames = [.. names] };
        }

        // A colony prisoner's custody facts: its resistance and interaction, the time held, its care and surgery
        // facts through the care read's own producers, its home faction and vanilla's harvest goodwill report
        // (Recipe_RemoveBodyPart reports -70 to HomeFaction when the pawn has a faction). A hosted guest carries
        // its medical care cap inputs.
        private static Obs.PersonCustody Custody(Pawn p, Faction player, Pawn_GuestTracker guest)
        {
            if (p.IsPrisonerOfColony)
            {
                var settings = p.playerSettings ?? throw new InvalidOperationException("Colony prisoner without player settings.");
                var interaction = guest.ExclusiveInteractionMode ?? throw new InvalidOperationException("Colony prisoner without an interaction mode.");
                var records = p.records ?? throw new InvalidOperationException("Colony prisoner without records.");
                var home = p.Faction == null ? null : p.HomeFaction;
                var faction = home != null && !home.IsPlayer ? home : null;
                var goodwill = faction != null && player.CanChangeGoodwillFor(faction, -70)
                    ? Math.Max(player.CalculateAdjustedGoodwillChange(faction, -70), -100 - player.GoodwillWith(faction))
                    : 0;
                return new Obs.PersonCustody.Prisoner(
                    Number(guest.Resistance),
                    NativePawnObservationTools.Id(interaction.defName),
                    (long)records.GetValue(RecordDefOf.TimeAsPrisoner),
                    NativeEnums.Care(settings.medCare),
                    NativePawnDetails.Conditions(p),
                    NativePawnDetails.Surgery(p) ?? throw new InvalidOperationException("Spawned colony prisoner without surgery facts."),
                    faction == null ? null : NativeRef.Of(NativePawnObservationTools.Id(faction.GetUniqueLoadID())),
                    goodwill);
            }
            if (p.HostFaction == player && !p.IsPrisoner && !p.IsSlave)
            {
                var settings = p.playerSettings ?? throw new InvalidOperationException("Colony guest without player settings.");
                return new Obs.PersonCustody.Guest(NativeEnums.Care(settings.medCare), NativePawnDetails.Conditions(p));
            }
            return new Obs.PersonCustody.None();
        }

        private static RimGovernor.Brain.Domain.PawnId PawnId(Pawn pawn) => new(NativePawnObservationTools.Id(pawn.GetUniqueLoadID()));

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
    }
}
