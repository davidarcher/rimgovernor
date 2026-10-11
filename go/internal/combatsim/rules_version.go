package combatsim

// AssemblyCSharpSHA256 is the SHA-256 of the pinned Assembly-CSharp.dll that
// every rule ported in this package was checked against (decompiled with
// ilspycmd). Each port names its game method in its doc comment; when the
// pinned assembly changes, re-check each against this hash and update it.
//
// Ports and their game sources:
//   - sight.go: Verse.GenSight.LineOfSight (halfX/halfZ overload),
//     Verse.ShootLeanUtility.LeanShootingSourcesFromTo and CellCanSeeCell,
//     Verse.GenGrid.CanBeSeenOver (Building.CanBeSeenOver),
//     Verse.GenSight.PointsOnLineOfSight (the IEnumerable overload).
//   - cover.go: Verse.CoverUtility.CalculateOverallBlockChance,
//     TryFindAdjustedCoverInCell, BaseBlockChance; Verse.CoverGrid.RecalculateCell;
//     Verse.GenGeo.AngleDifferenceBetween; Verse.IntVec3.AngleFlat.
//   - move.go: Verse.AI.Pawn_PathFollower.CostToMoveIntoCell, CostToPayThisTick,
//     SetupMoveIntoNextCell (cost carry-over); Verse.Pawn.TicksPerMove;
//     Verse.AI.PathGrid.CalculatedCostAt.
//   - combat.go: Verse.Verb.TryStartCastOn, WarmupComplete, VerbTick,
//     TryCastNextBurstShot, CanHitTargetFrom; Verse.Stance_Warmup, Stance_Busy,
//     Stance_Cooldown StanceTick; Verse.VerbProperties.AdjustedCooldown,
//     EffectiveMinRange; Verse.Pawn.Tick (stance, job, equipment phase order).
//   - shot.go: Verse.ShotReport.HitReportFor, HitFactorFromShooter,
//     AimOnTargetChance_*; Verse.VerbProperties.GetHitChanceFactor;
//     Verse.Verb_LaunchProjectile.TryCastShot; Verse.Projectile.Launch,
//     StartingTicksToImpact, CheckForFreeInterceptBetween, CheckForFreeIntercept,
//     CanHit, ImpactSomething; Verse.VerbUtility.InterceptChanceFactorFromDistance;
//     Verse.ArmorUtility.GetPostArmorDamage, ApplyArmor; Verse.GenMath.RoundRandom.
//
// Simplifications of these ports are listed in approximations.md.
const AssemblyCSharpSHA256 = "5CF1B5BE399D5B1C9C56CA72C9D35B4ECF307FEACF5859D04AC5A1AA5926356A"
