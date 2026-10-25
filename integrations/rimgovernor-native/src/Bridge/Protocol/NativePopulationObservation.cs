using System;
using System.Linq;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // Dedicated per-cycle population census on the typed native
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
        internal static RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot HandleReadPopulation(RimGovernor.Brain.GameOrders.Observations.PopulationRequest request)
        {
            if (!ValidatePopulation(request, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out _, out var failure))
                throw NativeFaults.Refused(failure);
            try { return Population(map, request); }
            catch (Exception error)
            {
                ModLog.Error("observe", "Population read failed: " + error);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Population could not be read completely: " + error.GetType().Name + ": " + error.Message) });
            }
        }

        internal static bool ValidatePopulation(RimGovernor.Brain.GameOrders.Observations.PopulationRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Valid identity scope is required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        // The population as a frame section: a read that throws fails the frame. Every person is read whole.
        internal static RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot Population(Map map, RimGovernor.Brain.GameOrders.Observations.PopulationRequest request)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).ToList();
            var persons = new System.Collections.Generic.List<RimGovernor.Brain.GameFacts.Observations.PopulationPerson>(people.Count);
            foreach (var p in people)
            {
                var guest = p.guest ?? throw new InvalidOperationException("Humanlike without a guest tracker.");
                persons.Add(new RimGovernor.Brain.GameFacts.Observations.PopulationPerson
                {
                    Pawn = PawnId(p),
                    Admitted = p.IsFreeColonist && p.Faction == player,
                    Guest = p.HostFaction == player,
                    Recruitable = guest.Recruitable,
                    IdeoId = p.Ideo == null ? null : NativePawnObservationTools.Id(p.Ideo.GetUniqueLoadID()),
                    WildMan = p.IsWildMan(),
                    Custody = Custody(p, player, guest),
                    WillJoinIfRescued = p.mindState.WillJoinColonyIfRescued,
                });
            }
            // Owned-pawn names: the census the unique-name planner reads.
            var names = NativePawnSettings.OwnedNamedPawns().Select(owned => new RimGovernor.Brain.GameFacts.Observations.OwnedName
            {
                PawnId = PawnId(owned),
                ShortName = NativePawnObservationTools.Text(owned.Name.ToStringShort),
                ThingId = owned.thingIDNumber,
            }).ToList();
            RimGovernor.Brain.GameFacts.Observations.ColonyIdeology ideology = ModsConfig.IdeologyActive
                ? new RimGovernor.Brain.GameFacts.Observations.ColonyIdeology.Installed(Find.IdeoManager.classicMode,
                    player.ideos?.PrimaryIdeo is Ideo ideo ? NativePawnObservationTools.Id(ideo.GetUniqueLoadID()) : null)
                : new RimGovernor.Brain.GameFacts.Observations.ColonyIdeology.NotInstalled();
            return new RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot { Persons = [.. persons], Ideology = ideology, OwnedNames = [.. names] };
        }

        // Custody owns resistance, interaction, time held and harvest goodwill. Health, surgery and care
        // stay on the canonical pawn. Recipe_RemoveBodyPart reports -70 to HomeFaction when one exists.
        private static RimGovernor.Brain.GameFacts.Observations.PersonCustody Custody(Pawn p, Faction player, Pawn_GuestTracker guest)
        {
            if (p.IsPrisonerOfColony)
            {
                var interaction = guest.ExclusiveInteractionMode ?? throw new InvalidOperationException("Colony prisoner without an interaction mode.");
                var records = p.records ?? throw new InvalidOperationException("Colony prisoner without records.");
                var home = p.Faction == null ? null : p.HomeFaction;
                var faction = home != null && !home.IsPlayer ? home : null;
                var goodwill = faction != null && player.CanChangeGoodwillFor(faction, -70)
                    ? Math.Max(player.CalculateAdjustedGoodwillChange(faction, -70), -100 - player.GoodwillWith(faction))
                    : 0;
                return new RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner(
                    Number(guest.Resistance),
                    NativePawnObservationTools.Id(interaction.defName),
                    (long)records.GetValue(RecordDefOf.TimeAsPrisoner),
                    faction == null ? null : NativeRef.Of(NativePawnObservationTools.Id(faction.GetUniqueLoadID())),
                    goodwill);
            }
            if (p.HostFaction == player && !p.IsPrisoner && !p.IsSlave)
                return new RimGovernor.Brain.GameFacts.Observations.PersonCustody.Guest();
            return new RimGovernor.Brain.GameFacts.Observations.PersonCustody.None();
        }

        private static RimGovernor.Brain.Domain.PawnId PawnId(Pawn pawn) => new(NativePawnObservationTools.Id(pawn.GetUniqueLoadID()));

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
    }
}
