using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // The per-pawn Biotech row block: the developmental stage, the genes, and
    // the mechanitor, mech and deathrest state. Inactive without Biotech; the
    // block is read whole and a failed read throws and fails the pawn read.
    internal static class NativeBiotechFacts
    {
        private static double Finite(double value, string what) =>
            double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException(what + " is not finite.") : value;

        internal static RimGovernor.Brain.GameFacts.Observations.PawnBiotechState State(Pawn pawn) =>
            ModsConfig.BiotechActive ? new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active(Pawn(pawn)) : new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Inactive();

        private static RimGovernor.Brain.GameFacts.Observations.PawnBiotech Pawn(Pawn pawn) => new RimGovernor.Brain.GameFacts.Observations.PawnBiotech
        {
            DevelopmentalStage = (RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage)(int)pawn.DevelopmentalStage,
            Genes = pawn.genes is Pawn_GeneTracker genes
                ? [.. genes.GenesListForReading.Select(g => new RimGovernor.Brain.GameFacts.Observations.PawnGene { DefName = g.def.defName, Active = g.Active })]
                : [],
            Mechanitor = pawn.mechanitor is Pawn_MechanitorTracker tracker
                ? new RimGovernor.Brain.GameFacts.Observations.PawnMechanitor
                {
                    UsedBandwidth = tracker.UsedBandwidth, TotalBandwidth = tracker.TotalBandwidth,
                    UsedBandwidthFromGestation = tracker.UsedBandwidthFromGestation, ControlGroups = tracker.controlGroups.Count,
                    ControlledMechs = [.. tracker.ControlledPawns.OrderBy(m => m.thingIDNumber).Select(m => NativeRef.Thing(m))],
                }
                : null,
            Mech = pawn.RaceProps.IsMechanoid && MechanitorUtility.EverControllable(pawn) ? Mech(pawn) : null,
            Deathrest = pawn.genes?.GetFirstGeneOfType<Gene_Deathrest>() is Gene_Deathrest gene
                ? new RimGovernor.Brain.GameFacts.Observations.PawnDeathrest { Deathresting = gene.DeathrestNeed?.Deathresting ?? false, Capacity = gene.DeathrestCapacity }
                : null,
        };

        private static RimGovernor.Brain.GameFacts.Observations.PawnMech Mech(Pawn pawn) => new RimGovernor.Brain.GameFacts.Observations.PawnMech
        {
            Overseer = MechanitorUtility.GetOverseer(pawn) is Pawn overseer ? NativeRef.Thing(overseer) : null,
            WorkMode = MechanitorUtility.GetMechWorkMode(pawn)?.defName,
            Energy = pawn.needs?.TryGetNeed<Need_MechEnergy>() is Need_MechEnergy energy ? new RimGovernor.Brain.GameFacts.Observations.PawnMechEnergy.Level { Fraction = Finite(energy.CurLevelPercentage, "mech energy") } : new RimGovernor.Brain.GameFacts.Observations.PawnMechEnergy.NoEnergyNeed(),
            Group = MechanitorUtility.GetMechControlGroup(pawn) is MechanitorControlGroup group ? Group(group) : null,
        };

        // The group's own recharge band; a game without the field fails the
        // read loudly instead of inventing a threshold.
        private static RimGovernor.Brain.GameFacts.Observations.PawnMechGroup Group(MechanitorControlGroup group)
        {
            var thresholds = BridgeCommon.PrivateInstanceField(typeof(MechanitorControlGroup), "mechRechargeThresholds")
                ?? throw new InvalidOperationException("MechanitorControlGroup.mechRechargeThresholds not found");
            var band = (FloatRange)(thresholds.GetValue(group) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
            return new RimGovernor.Brain.GameFacts.Observations.PawnMechGroup { Index = group.Index, RechargeBelow = Finite(band.min, "recharge below"), RechargeAbove = Finite(band.max, "recharge above") };
        }
    }
}
