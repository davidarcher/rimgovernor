package combatsim

// AssemblyCSharpSHA256 is the SHA-256 of the pinned Assembly-CSharp.dll that
// every rule ported in this package was checked against (decompiled with
// ilspycmd). Each port names its game method in its doc comment; when the
// pinned assembly changes, re-check each against this hash and update it.
//
// Ports and their game sources:
//
//   - sight.go: Verse.GenSight.LineOfSight (halfX/halfZ overload),
//     Verse.ShootLeanUtility.LeanShootingSourcesFromTo and CellCanSeeCell,
//     Verse.GenGrid.CanBeSeenOver (Building.CanBeSeenOver),
//     Verse.GenSight.PointsOnLineOfSight (the IEnumerable overload).
//
//   - cover.go: Verse.CoverUtility.CalculateOverallBlockChance (in float, as the
//     game and the native reply carry it, #2994),
//     TryFindAdjustedCoverInCell, BaseBlockChance; Verse.CoverGrid.RecalculateCell;
//     Verse.GenGeo.AngleDifferenceBetween; Verse.IntVec3.AngleFlat.
//
//   - move.go: Verse.AI.Pawn_PathFollower.CostToMoveIntoCell, CostToPayThisTick,
//     SetupMoveIntoNextCell (cost carry-over); Verse.Pawn.TicksPerMove;
//     Verse.AI.PathGrid.CalculatedCostAt; Verse.AI.Pawn_PathFollower.StartPath and
//     PatherTick (the path request claimed a tick late, #2933).
//
//   - collision.go: Verse.AI.Pawn_PathFollower.PatherTick (WillCollideWithPawnAt,
//     the 15-node and 180-tick give-up, NeedNewPath), RimWorld.PawnUtility
//     ShouldCollideWithPawns, PawnBlockedBy, PawnsCanShareCellBecauseOfBodySize;
//     Verse.PathGridDoorsBlockedJob and Verse.PathFinderJob (Cost_Blocked).
//
//   - combat.go: Verse.Verb.TryStartCastOn, WarmupComplete, VerbTick,
//     TryCastNextBurstShot, CanHitTargetFrom; Verse.Stance_Warmup, Stance_Busy,
//     Stance_Cooldown StanceTick; Verse.VerbProperties.AdjustedCooldown,
//     EffectiveMinRange; Verse.Pawn.Tick (stance, job, equipment phase order);
//     Verse.Pawn.TryStartAttack, Verse.AI.JobDriver_AttackStatic and
//     Verse.AI.Pawn_JobTracker.StartJob (when a job's verb starts, #2914).
//
//   - shot.go: Verse.ShotReport.GetRandomCoverToMissInto;
//     Verse.ShootLine.ChangeDestToMissWild;
//     Verse.ShootTuning.MissDistanceFromAimOnChanceCurves (the wild and
//     covered shot, #2937); Verse.ShotReport.HitReportFor, HitFactorFromShooter,
//     AimOnTargetChance_*; Verse.VerbProperties.GetHitChanceFactor;
//     Verse.Verb_LaunchProjectile.TryCastShot; Verse.Projectile.Launch,
//     StartingTicksToImpact, CheckForFreeInterceptBetween, CheckForFreeIntercept,
//     CanHit, ImpactSomething; Verse.VerbUtility.InterceptChanceFactorFromDistance;
//     Verse.ArmorUtility.GetPostArmorDamage, ApplyArmor; Verse.GenMath.RoundRandom;
//     Verse.Thing.DoTick and TickInterval, Verse.GenTicks.GetCameraUpdateRate,
//     Verse.Gen.RandomHorizontalVector, Verse.Altitudes.AltitudeFor (the
//     projectile's 15-tick phase, 3D flight and scatter, #2933).
//
//   - thingid.go: Verse.Gen.HashOffset (Gen.HashCombineInt) and the
//     Thing.IsHashIntervalTick rule that sets a projectile's tick phase from
//     its thing id; Verse.Pawn.PostApplyDamage and Pawn_HealthTracker
//     .DropBloodFilth (the filth that takes ids between shots, #2936).
//
//   - enemy.go: Verse.AI.Stance_Warmup and Pawn_MindState.lastAttackedTarget
//     (EnemyConfig.FirstTargets, #3007), Verse.AI.AttackTargetFinder.BestAttackTarget,
//     GetAvailableShootingTargetsByScore, GetShootingTargetScore,
//     FriendlyFireConeTargetScoreOffset, CanSee; RimWorld.JobGiver_AIFightEnemy
//     TryGiveJob, UpdateEnemyTarget, ShouldLoseTarget, FindAttackTarget;
//     RimWorld.JobGiver_AIFightEnemies.TryFindShootingPosition;
//     RimWorld.JobGiver_AIGotoNearestHostile; Verse.AI.CastPositionFinder
//     TryFindCastPosition, EvaluateCell, CastPositionPreference;
//     Verse.AI.JobDriver_Wait.CheckForAutoAttack; Verse.AI.Pawn_JobTracker
//     (DistanceIntervalCurve, expiry); RimWorld.LordJob_AssaultColony.CreateGraph,
//     Trigger_FractionColonyDamageTaken; Verse.CellLine; Verse.VerbUtility
//     CalculateAdjustedForcedMiss; the Core AssaultColony DutyDef radii;
//     Verse.AI.Group.Lord.SetJob (the autoFlee LordToil_PanicFlee
//     transition), Notify_PawnLost, Trigger_FractionPawnsLost,
//     RimWorld.LordToil_PanicFlee (the panic flee, #2983);
//     Verse.Pawn_HealthTracker.PostApplyDamage and the native
//     SupervisedPlayCombatStops serious-injury hit floor (#2985), and its
//     summary-health and bleed-rate triggers: Verse.SummaryHealthHandler,
//     Verse.Hediff_Injury.BleedRate, Verse.HediffSet.CalculateBleedRate,
//     RimWorld.Bullet.Impact (a null HitPart, so no vital-part trigger, #2991).
//
//   - body.go, shot.go (damage): Verse.Pawn_HealthTracker (ShouldBeDead,
//     ShouldBeDowned, InPainShock, CheckForStateChange, LethalDamageThreshold),
//     Verse.HediffSet (GetPartHealth, GetRandomNotMissingPart, CalculatePain,
//     PartIsMissing), Verse.Hediff_Injury (PainOffset), Verse.Hediff_MissingPart
//     (PostAdd, PainOffset, IsFresh), Verse.DamageWorker_AddInjury
//     (ApplyDamageToPart, FinalizeAndAddInjury, ShouldReduceDamageToPreservePart,
//     ReduceDamageToPreserveOutsideParts, CheckDuplicateDamageToOuterParts),
//     Verse.BodyPartDef.GetMaxHealth, Verse.BodyPartRecord.GetChildParts and
//     GetConnectedParts, RimWorld.PawnCapacityUtility (CalculateCapacityLevel,
//     CalculatePartEfficiency, CalculateTagEfficiency, CalculateLimbEfficiency)
//     with the Consciousness, Moving, Manipulation, BloodPumping, Breathing,
//     BloodFiltration and Metabolism capacity workers, and
//     Verse.PawnCapacitiesHandler.CanBeAwake (the per-part injury model, #2986);
//     Verse.HediffGiver_Bleeding.OnIntervalPassed, Verse.Hediff.Severity and the
//     BloodLoss HediffDef stages (blood loss, #3000).
//
//   - thingid.go, enemy.go, run.go (job cadence): Verse.AI.Pawn_JobTracker
//     .JobTrackerTickInterval and its job expiry, Verse.Gen.IsHashIntervalTick,
//     Verse.AI.JobDriver_Wait (CheckForAutoAttack at job start, on
//     Notify_StanceChanged and on each tracker tick, #3000); combat.go:
//     Verse.Stance_Warmup.StanceTick (a warmup that can no longer hit drops to
//     Stance_Mobile and the raider's self-started order dies with it, #3001).
//
//   - accuracy.go: RimWorld.StatWorker.GetValueUnfinalized and FinalizeValue for
//     ShootingAccuracyPawn (the capacityOffsets, RimWorld.PawnCapacityOffset
//     .GetOffset, and the postProcessCurve from the Core
//     Stats_Pawns_Combat.xml), with RimWorld.PawnCapacityWorker_Sight
//     (PartEfficiencySpecialWeight 0.75) in body.go: an injured shooter's
//     Manipulation and Sight lower its accuracy per cell (#3006).
//
// Simplifications of these ports are listed in approximations.md.
const AssemblyCSharpSHA256 = "5CF1B5BE399D5B1C9C56CA72C9D35B4ECF307FEACF5859D04AC5A1AA5926356A"

// RulesVersion is the version of the ported ruleset as a whole. Bump it with
// any change to a port or an approximation that can move an outcome (a rule
// fix, a new modeled behavior): a calibration pass (combatsim/calibrate) is
// recorded under it and is stale after a bump.
const RulesVersion = 17
