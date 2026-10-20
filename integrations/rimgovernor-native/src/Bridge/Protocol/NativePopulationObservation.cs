#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    // ReadStatus/ReadHusbandry/ReadPrisonerInteraction; a paginated population
    // is deferred to whatever candidate search eventually needs one.
    internal static class NativePopulationObservation
    {
        internal static Obs.PopulationReply HandleReadPopulation(Obs.PopulationRequest request)
        {
            if (!ValidatePopulation(request, out var failure0)) { ReadFailureLog.Note("population", failure0.Detail); return new Obs.PopulationReply(); }
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
            {
                ReadFailureLog.Note("population", failure.Detail);
                return new Obs.PopulationReply();
            }
            try { return new Obs.PopulationReply { Observed = Population(map, request, context) }; }
            catch (Exception error) { ModLog.Error("observe", "Population read failed: " + error); ReadFailureLog.Note("population", error); return new Obs.PopulationReply(); }
        }

        internal static bool ValidatePopulation(Obs.PopulationRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope is required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        // The population as a frame section: a read that throws fails the frame.
        internal static Obs.PopulationSnapshot Population(Map map, Obs.PopulationRequest request, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).ToList();
            var snapshot = new Obs.PopulationSnapshot { Context = context };
            foreach (var p in people)
            {
                var person = new Obs.PopulationPerson { Pawn = NativePawnObservationTools.Ref(p), Admitted = p.IsFreeColonist && p.Faction == player, Guest = p.HostFaction == player };
                if (p.guest != null)
                {
                    person = person with { Recruitable = p.guest.Recruitable };
                    person = person with { Resistance = Number(p.guest.Resistance) };
                    var interaction = p.guest.ExclusiveInteractionMode?.defName;
                    if (interaction != null) person = person with { Interaction = NativePawnObservationTools.Id(interaction) };
                    // Time held so far (the TimeAsPrisoner record, in ticks): the
                    // routine release path's "held N days with resistance
                    // unbroken" clock. Only a current prisoner carries it.
                    if (p.IsPrisoner && p.records != null) person = person with { PrisonerTicks = (long)p.records.GetValue(RecordDefOf.TimeAsPrisoner) };
                }
                if (p.Ideo != null) person = person with { IdeoId = NativePawnObservationTools.Id(p.Ideo.GetUniqueLoadID()) };
                // Prospect facts: the prisoners family weighs a prisoner's
                // skills, traits, age and health against the free colonists';
                // a hostile's skills rank it as a lance target.
                if (p.IsPrisonerOfColony || person.Admitted.GetValueOrDefault() || p.HostileTo(player))
                {
                    person = person with { WildMan = p.IsWildMan() };
                    person = person with { Biography = NativePawnDetails.Biography(p) };
                    if (p.health?.summaryHealth != null) person = person with { HealthSummary = Number(p.health.summaryHealth.SummaryHealthPercent) };
                    // After combat: a luciferium addict is stripped and
                    // finished, not captured; every raider is stripped first.
                    person = person with { LuciferiumAddicted = CombatMirror.HasHediff(p, "LuciferiumAddiction") };
                    person = person with { WearingApparel = p.apparel?.WornApparel.Count > 0 };
                }
                // Organ harvest facts: a colony prisoner's surgery
                // facts through the care read's own producer, its home faction
                // and vanilla's harvest goodwill report (Recipe_RemoveBodyPart
                // reports -70 to HomeFaction when the pawn has a faction).
                // Medical care cap inputs: colony prisoners and hosted guests.
                if (p.IsPrisonerOfColony || (p.HostFaction == player && !p.IsPrisoner && !p.IsSlave))
                {
                    if (p.playerSettings != null) person = person with { MedicalCare = NativeEnums.Care(p.playerSettings.medCare) };
                    person = person with { Conditions = NativePawnDetails.Conditions(p) };
                }
                if (p.IsPrisonerOfColony)
                {
                    person = person with { Surgery = NativePawnDetails.Surgery(p) };
                    if (p.playerSettings != null) person = person with { MedicalCare = NativeEnums.Care(p.playerSettings.medCare) };
                    var home = p.Faction == null ? null : p.HomeFaction;
                    person = person with { HarvestGoodwillChange = 0 };
                    if (home != null && !home.IsPlayer)
                    {
                        person = person with { Faction = NativeRef.Of(NativePawnObservationTools.Id(home.GetUniqueLoadID())) };
                        if (player.CanChangeGoodwillFor(home, -70))
                            person = person with { HarvestGoodwillChange = Math.Max(player.CalculateAdjustedGoodwillChange(home, -70), -100 - player.GoodwillWith(home)) };
                    }
                }
                person = person with { WillJoinIfRescued = p.mindState.WillJoinColonyIfRescued };
                snapshot = snapshot with { Persons = [.. snapshot.Persons, person] };
            }
            // Owned-pawn names: the census the unique-name planner reads.
            foreach (var owned in NativePawnSettings.OwnedNamedPawns())
                snapshot = snapshot with { OwnedNames = [.. snapshot.OwnedNames, new Obs.OwnedName { PawnId = NativePawnObservationTools.Id(owned.GetUniqueLoadID()), ShortName = NativePawnObservationTools.Text(owned.Name.ToStringShort), ThingId = owned.thingIDNumber }] };
            snapshot = snapshot with { IdeologyActive = ModsConfig.IdeologyActive };
            if (ModsConfig.IdeologyActive)
            {
                snapshot = snapshot with { ClassicIdeoMode = Find.IdeoManager.classicMode };
                var ideo = player.ideos?.PrimaryIdeo;
                if (ideo != null)
                {
                    snapshot = snapshot with { ColonyIdeoId = NativePawnObservationTools.Id(ideo.GetUniqueLoadID()) };
                }
            }
            return snapshot;
        }

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;    }
}
