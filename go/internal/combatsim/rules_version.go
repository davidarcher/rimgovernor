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
//   - cover.go: Verse.CoverUtility.CalculateOverallBlockChance,
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
//   - shot.go: Verse.ShotReport.HitReportFor, HitFactorFromShooter,
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
//   - enemy.go: Verse.AI.AttackTargetFinder.BestAttackTarget,
//     GetAvailableShootingTargetsByScore, GetShootingTargetScore,
//     FriendlyFireConeTargetScoreOffset, CanSee; RimWorld.JobGiver_AIFightEnemy
//     TryGiveJob, UpdateEnemyTarget, ShouldLoseTarget, FindAttackTarget;
//     RimWorld.JobGiver_AIFightEnemies.TryFindShootingPosition;
//     RimWorld.JobGiver_AIGotoNearestHostile; Verse.AI.CastPositionFinder
//     TryFindCastPosition, EvaluateCell, CastPositionPreference;
//     Verse.AI.JobDriver_Wait.CheckForAutoAttack; Verse.AI.Pawn_JobTracker
//     (DistanceIntervalCurve, expiry); RimWorld.LordJob_AssaultColony.CreateGraph,
//     Trigger_FractionColonyDamageTaken; Verse.CellLine; Verse.VerbUtility
//     CalculateAdjustedForcedMiss; the Core AssaultColony DutyDef radii.
//
// Simplifications of these ports are listed in approximations.md.
const AssemblyCSharpSHA256 = "5CF1B5BE399D5B1C9C56CA72C9D35B4ECF307FEACF5859D04AC5A1AA5926356A"

// RulesVersion is the version of the ported ruleset as a whole. Bump it with
// any change to a port or an approximation that can move an outcome (a rule
// fix, a new modeled behavior): a calibration pass (combatsim/calibrate) is
// recorded under it and is stale after a bump.
const RulesVersion = 7
