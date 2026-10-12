# combatsim approximations

Every place the ranged-combat port (`combat.go`, `shot.go`) departs from the
game, and the labeled simplification that stands in. A departure not listed
here is a bug. The calibration gate (#2844) decides which of these matter; each
is a candidate to replace by a faithful port. Game sources are in
`rules_version.go`.

## Injury and health

- **A1. Two thresholds replace the health system.** A pawn accumulates post-armor
  damage and is downed at `DamageToDown`, dead at `DamageToKill` (both supplied
  per pawn by the caller, `DamageToKill >= DamageToDown`). The game downs and
  kills through body-part destruction, consciousness, moving and manipulation
  capacities, pain and blood loss. No hediffs, no body parts. A hostile that
  crosses `DamageToDown` also takes the death-on-downed roll (A33), which is
  how raiders die in practice; `DamageToKill` is rarely reached.
- **A2. No bleeding, pain, infection or recovery.** Damage never decays and
  nothing is tended; a downed pawn does not bleed out.
- **A3. Damage is applied whole.** The game splits a hit across body parts and
  scatters a share to neighbours; here the full post-armor amount counts once,
  plus the outer-part duplicates of A34.
- **A4. A downed pawn stays where it fell** and is not carried, crawling or
  rescued. A downed or dead pawn does not collide (`ShouldCollideWithPawns`), so
  it never blocks a lane; a downed one stays on its cell's list, where shots
  still reach it (the 0.1 lying factor), and a dead one leaves the list (A35).

## Armor

- **A5. Armor is body-part-agnostic.** `ArmorUtility.GetPostArmorDamage` tests
  each apparel's `CoversBodyPart` for the part hit; here the caller flattens the
  pawn's layers into one ordered list of ratings (outermost first, natural last).
- **A6. No deflect-to-blunt, no apparel damage.** A halved sharp hit stays a
  damage amount; the 25% wear on the apparel is skipped (and with it the RNG
  draw the game spends there). Only the stop/halve/pass roll is ported.

## Hit resolution

- **A7. Wild and cover misses fly on; the forced miss radius is not ported.**
  A wild shot is sent to `ShootLine.ChangeDestToMissWild`'s cell (a random
  point `MissDistanceFromAimOnChanceCurves` cells from the target's cell centre,
  not behind the shooter, cut short at the first filled cell when the shooter
  cannot see it) with the NonTargetWorld flag and, on a coin flip, NonTargetPawns;
  a covered shot is sent at a cover thing drawn by block chance
  (`GetRandomCoverToMissInto`) with both. Each then flies like any projectile
  (A9): a wall stops it, a sandbag (fill over 0.2) intercepts at its fill percent
  next to the destination and 0.15 of that further out, bystanders by the
  usual pawn chances (friendly ones scaled by the friendly-fire factor), and at
  the destination `ImpactSomething` strikes the cover, or rolls each pawn and
  thing on a wild cell. A shot that ends in a thing is `Event.StopsInThing`.
  Recorded shots (#2933): 38 percent of the raiders' shots stop in a sandbag
  or other non-pawn thing, none of the colonists' do (0.1 percent); the sim has
  37.6 and 0.0 percent. Not ported: weapons with `ForcedMissRadius` above 0.5
  (mortars; their imports stay unsupported), the lean cell as the wild line's
  source (A8), and the `canGoWild` and `canHitNonTargetPawnsNow` verb flags
  (both true for the ordinary attack job).
- **A8. The line is cell centre to cell centre, in 3D, with the game's
  scatter.** Projectiles leave the shooter's cell centre (not the lean cell,
  `PawnLeaner`) and aim at the target's cell centre plus
  `RandomHorizontalVector(0.3)` (uniform within 0.3 on each axis, drawn from the
  physical stream). `StartingTicksToImpact` divides the 3D distance by the
  speed: the origin is the pawn's `DrawPos`, at the `Pawn` altitude layer
  (23 x 0.36585367, 8.41), the destination at y 0, rounded up with a minimum of
  one tick. At 20 cells that is 39 to 40 ticks, not the flat 37 (#2933).
- **A9. Interception is decided at launch; projectiles tick every 15 ticks.**
  The flight is walked once, against the pawns standing at the moment of the
  shot; a pawn that steps into or out of the line while the projectile flies is
  not seen. The victim and landing tick are fixed then. Damage is applied at the
  landing tick. A projectile off camera is ticked by `Thing.TickInterval` every
  `GenTicks.GetCameraUpdateRate` = 15 ticks at the phase of its thing id hash,
  `ticksToImpact` falling by the ticks elapsed, so it lands on the first phase
  tick at or after launch + its 3D flight (0 to 14 ticks later) and is tested
  for interception per 15-tick segment, the last one included. The phase
  residue is `(tick + Gen.HashOffset(thingID)) % 15 == 0`, and the sim replays
  it from the id (#2936): every recorded landing (1526 of 1526; 1508 are the
  first such tick at or after launch + ticks to impact, the rest interceptions)
  is on its id's phase. Ids come from one counter shared by both sides: a run
  starts at 7308 plus 0 to 3 (the four first ids of the 20 recorded seeds, 7 /
  7 / 4 / 2), every launched projectile (wild, cover or flying) takes the next
  id, and a damaging hit takes one more with probability 356/410 (the recorded
  extra ids over damaging impacts: 0.5 for `PostApplyDamage`'s blood filth,
  the rest bleeding drops, lumped into the hit; filth stacking on an existing
  one, a corpse or dropped gear are not told apart). The start id is the
  `lab-ranged` checkpoint's; other scenarios inherit it. Draws come from the
  stream of its own (`Streams.Phase`). The sim's flights run about one tick
  short of the recorded ones (median 45 against 46, mean 45.4 against 46.2);
  wild and cover shots are flown too since A7, so the mix is the game's.
- **A10. Only pawns intercept an aimed shot.** A shot aimed at a pawn carries
  the IntendedTarget and NonTargetPawns hit flags only, so walls, doors and
  cover never intercept it in flight; they matter through line of fire
  (`CellCanSeeCell`) and the cover roll. This is the game's rule, noted because
  it surprises. Wild and covered shots carry NonTargetWorld and do strike
  them (A7).
- **A11. A pawn without `Combat` is not in the fight.** Shots pass through it;
  it cannot be a target.
- **A12. Everyone stands or lies.** Posture is standing, or lying when downed or
  dead; no crouching or kneeling factors. Faction hostility is "other side".
- **A13. No darkness offset, no gas.** `offsetFromDarkness` (Ideology) is 0 and
  `factorFromCoveringGas` is 1. Weather is the single `WeatherAccuracy`
  multiplier; no wind (accuracy does not depend on it in the game either).
- **A14. float64 instead of float32.** The game computes in single precision;
  differences are below the tests' 1e-5 tolerance but not zero. Seconds are
  rounded to ticks with banker's rounding as `Mathf.RoundToInt` does.

## Verb timing

- **A15. A shooter that cannot hit waits.** The game's attack job may reposition
  or fail; here the pawn idles until the target is in range and line of fire,
  and an out-of-sight warmup is dropped and retried. A burst stops when sight is
  lost (`stopBurstWithoutLos`) and the cooldown starts.
- **A16. Stuns, staggering, mental breaks and equipment swaps do not exist.**
  Nothing interrupts a burst except losing sight of a live target.
- **A17. No ammo, reloading or fuel.**
- **A18. A burst continues against a pawn it has downed** (it stops only when the
  target is dead or out of sight), as in the game; the attack order ends at the
  next idle tick.
- **A19. Skill learning, noise and clamor are not simulated.**

## Policy adapter (`adapter.go`, `geometry.go`, `run.go`)

- **A20. Health is the share of the damage budget left.** `View` reports
  `1 - damage/DamageToKill` (0 dead); the game's health scale is capacity-based.
  Bleeding, pain, tending and medical skill have no sim state and are not
  reported as facts the policy could misread.
- **A21. An idle drafted colonist shoots on its own.** A drafted pawn with no job
  runs Wait_Combat (what `hold_position` starts, and what a finished `move`
  falls back to), whose auto-attack picks a hostile in reach. `Autonomy` gives
  every idle, not walking, plan-owned colonist that is not on hold fire an
  attack on the nearest hostile it can hit (ties to the earlier scenario pawn).
  It used to cover only `hold_position` pawns, so colonists the policy sent to
  a cell never fired and the sim shot about a quarter as often as the game
  (#2899). The game's target scoring is not ported for colonists (the hostiles'
  is, E1-E8).
- **A22. Only two stop kinds are raised.** `downed` (a colonist downed or
  killed) and `entered_range` (a hostile came within a colonist's weapon range,
  or a colonist's weapon reached it; distance only, no sight, once per hostile
  each way, the first scan recording the start). The native serious-injury,
  shield, lord, melee, explosive and mental-state stops need state the sim does
  not hold. A backstop (default 120 ticks) asks the policy again.
- **A23. Geometry is computed from the current sim world.** Sight lines and
  cover use the ports above; proposals follow the native roles' filters and
  orderings under the same caps (64 cells, 16 hostiles, radius 12). A
  rescue route is the walker's A* path cut at the first cell touching the goal,
  not the game's path-to-touch. Only ranged `move`, `attack`, `stop`,
  `hold_position` and `fire_mode` orders execute; every other kind is refused
  `native_refused`. Unknown pawn facts (kind, melee power, colonist armor) make
  the fight unsupported rather than being defaulted.

## Enemy AI for ranged raiders (`enemy.go`)

Ported from the pinned assembly (hash and per-method sources in `enemy.go` and
`rules_version.go`): `AttackTargetFinder` scoring and the weighted draw,
`JobGiver_AIFightEnemy` (target retention, shooting position, wait vs. goto),
`CastPositionFinder`, `JobGiver_AIGotoNearestHostile`, the Wait_Combat
auto-attack, and the `LordJob_AssaultColony` give-up and damage-fraction
transitions. Every draw comes from the enemy stream (E6 lists what is drawn).

- **E1. Decisions happen at Step boundaries.** `EnemyAI.Orders` runs before
  each Step. A job that ends mid-Step is replaced at the next boundary, and the
  auto-attack's four-tick search (staggered by `thingIDNumber` in the game) runs
  at the first boundary at or after four ticks since the last, or at once when a
  burst's cooldown has just ended (`Notify_StanceChanged`). With `StepTicks`
  1 (the lab's) there is no difference; a larger step delays by up to one step.
- **E2. A self-started attack is one burst.** `Order.Once` models
  `Pawn.TryStartAttack`: after the cooldown the pawn drops its target and the
  AI scores again. Only the raider AI uses it.
- **E3. Targets are colonist combatants that stand.** The game's candidates
  also include buildings, turrets, animals and non-combatants (no sim state), and
  downed pawns (score minus 50). The sim cannot shoot a downed pawn (A18), so
  downed pawns are not candidates and a downed target is lost at once
  (`ThreatDisabled`); the downed term, `NonCombatantScore`, juvenile and
  blast-radius friendly-fire terms (`ai_AvoidFriendlyFireRadius`) are not
  ported. `TargetPriorityFactor` is 1 for pawns. Candidates are listed in
  scenario order, which fixes the weighted draw's walk (the game uses
  attack-target cache order).
- **E4. Sight and the shot line are cell to cell.** `CanSee` is
  `CellCanSeeCell` (no multi-cell `CalcShootableCellsOf`); the friendly-fire cone
  uses the shooter's cell as the shot line's source (the game uses the lean cell)
  and `GenSight.PointsOnLineOfSight` for the line. Fog, gas, darkness, roofs'
  thickness and `NeedReachable` door bashing are absent. Reachability is a
  flood of walkable cells with the pathfinder's neighbour rules, ignoring pawns
  and `Danger`.
- **E5. Shooting-position search drops what the sim lacks.** No avoid grid,
  allowed area, region radius, `KnownDangerAt`, fire, or `aiAvoidCover` (false
  for humanlike raiders). `CanReserve` is "no other pawn stands there and no
  other raider is walking there". The game's `CastPositionPreference` multiplies
  by 0.4 when `rangeFromCasterToCellSquared` exceeds the caster-to-target
  distance, but that static is only set by requests with `maxRangeFromCaster`,
  which AIFightEnemies does not use, so it holds a stale value from an earlier
  request; the port treats it as 0 (factor not applied). Single precision is used
  only for the `CellLine` side test, where it decides ties.
- **E6. Jobs are three kinds, with the game's lengths.** `Wait_Combat` and the
  shooting-position `Goto` last 450-550 ticks (inclusive, uniform); the
  nearest-hostile `Goto` expires by the distance curve (30/120/600 ticks at
  0/50/100 Manhattan cells, linear between) and ends on arrival. The lord's
  thresholds are drawn once in `NewEnemyAI` (give-up ticks, then damage
  fraction), and each Wait/Goto length when the job starts. The expiry's
  `IsHashIntervalTick` alignment, `expireRequiresEnemiesNearby`, damage-driven
  think overrides (`checkOverrideOnDamage`), job queues, weapon swaps, drugs,
  abilities, trashing buildings, sapping, kidnapping and stealing are not
  modelled; the raid is a plain `AssaultColony` duty with `JobGiver_AIFightEnemies`
  radii 65 (acquire) and 72 (keep), and no `chaseTarget`.
- **E7. The raid flees on two triggers, as one lord.** After
  `AssaultTimeBeforeGiveUp` (26000-38000 ticks) or when the colonists' summed
  post-armor damage exceeds `max(initialColonyHealthTotal x U(0.25, 0.35), 900)`
  (`Trigger_FractionColonyDamageTaken`), every raider walks to `Exit` at a jog and
  leaves the map (`OrderLeave`; counted as fled in the metrics). The game's
  damage is `DamageWatcher.DamageTakenEver`, which counts every damaged player
  thing; the sim counts colonist pawns only, and `ColonyHealthTotal` is the
  caller's value (`WealthWatcher.HealthTotal` is not derivable here). Fleeing
  raiders do not fight back, and no individual morale or mental-break flee
  exists. `Trigger_TicksPassed` is tested with `>=`.
- **E8. Unarmed hostiles and melee do nothing.** A hostile without a ranged
  weapon gets no orders from the AI.

## Scenario importer (`simimport/`)

The importer derives what the sim holds no literals for from the native
DefinitionCatalog; every derived value is listed with its source in
`Result.Trace`. A value it cannot derive fails the import with `ErrUnsupported`.

- **A24. A melee weapon imports as unarmed.** The pawn keeps its catalog melee
  DPS as `MeleePower` and is named in `Result.Unmodeled`; no melee verb is
  simulated. Explosive, launcher, one-use, EMP and incendiary weapons, turrets
  and non-humanlike races are refused, as is a spec with no ranged weapon at all.
- **A25. Injury thresholds are the pain-shock and lethal-damage rules only.**
  `DamageToKill` = 150 x HealthScale (healthScaleFactor x baseHealthScale).
  `DamageToDown` = PainShockThreshold x HealthScale / the damage hediff's
  `injuryProps.painPerSeverity`. Blood loss, consciousness, vital parts and
  per-part health are ignored, and a spec may use one damage def only.
- **A26. Armor is flattened.** Worn apparel outermost first, then the race's
  natural armor, each as the definition-level rating of the damage's armor
  category stat; the composite fact is 1 - product(1 - r). Quality, hit points
  and per-body-part coverage are ignored.
- **A27. Weapons are read at normal quality.** Range is capped by the weather
  `maxRangeCap`; cooldown, accuracy bands, damage and AP multipliers come from
  the stat evaluator on the weapon def and stuff. The wielder's range stats are
  not applied. Projectile damage and AP follow `GetDamageAmount` and
  `GetArmorPenetration` (0.015 per damage fallback); speed is speed/100.
- **A28. Pawns are baseline healthy adults.** The adult stage is the race's
  last life stage; no traits, hediffs, needs, mood or gear beyond the listed
  apparel and weapon. Move speed is the MoveSpeed stat on that context.
- **A29. MeleePower and FrontLine come from the catalog DPS and skills.** A pawn
  is a front-liner when it has no ranged weapon and Melee >= Shooting; both
  skills must be stated.
- **A30. Things are one per cell and stuff is ignored.** Fill percent, path
  cost and passability come from the ThingDef (fillage thresholds 0.01/0.99,
  doors by thingClass); stuff does not change them.
- **A31. Lab assumptions are caller-stated.** Skills, an assault lord toil
  for hostiles, hostile apparel destroyed, colonist apparel and the difficulty
  friendly-fire factor are inputs (`combatlab.SimEnv`), not readings; the last
  two stay unknown, and the import fails, until stated. The lab staging sets
  every skill to 8, but the real runs the calibration gate compares with (the
  metrics cases) staff the lab with the superpawn crew afterwards (`test/lab_crew`,
  every skill 20, plus Jogger and Industrious), so `CalibrationEnv` states
  colonist skill 20 and hostile skill 8 (#2906). Stating 8 for both made the
  sim's colonists aim on target 23% of the time at 25 cells where the real crew
  did about 48%, and the sim landed two thirds of the real hit volume. The crew's
  traits only add move speed (not modeled; A28).

- **A32. The raid lord's constants come from the catalog, its exit and colony
  health are derived.** `Spec.Raid` makes the importer set `Fight.EnemyAI`:
  the give-up range is `LordJob_AssaultColony.AssaultTimeBeforeGiveUp`, and
  `ColonyHealthTotal` follows `WealthWatcher.HealthTotal` (100 per free colonist
  at full health plus the hit points of every player artificial building, here
  the def-level `MaxHitPoints` stat: stuff and damage are ignored). The exit is
  caller-stated; the lab states the map-edge cell nearest the raiders' centroid
  (the game picks its own exit spot).
- **A33. Death on downed is ported, its inputs are caller-stated.**
  `Pawn_HealthTracker.CheckForStateChange` rolls once when a non-colony,
  non-prisoner pawn first becomes downable from violence: the pawn dies with
  `HealthTuning.DeathOnDowned_NonColonyHumanlikeFromPopulationIntentCurve` (or
  the wavering-prisoners curve) at `PopulationIntent`, times the difficulty
  `enemyDeathOnDownedChanceFactor`. `Spec.DeathOnDowned` states the intent,
  factor and unwavering flag; the lab states intent 0, factor 1, wavering (the
  quiet storyteller at four colonists on day zero; 0.85, matching 56 of 67
  raiders that crossed the pain-shock line dying in the real ledger). Kind
  `forceDeathOnDowned` and `overrideDeathOnDownedChance` overrides, animals,
  and the other death paths (vital part loss, brain hits, blood loss) are not
  modeled; `DamageToKill` is therefore rarely reached.
- **A34. Hit layers duplicate injuries to outer parts.** A bullet's
  `harmAllLayersUntilOutside` (`DamageWorker_AddInjury.CheckDuplicateDamageToOuterParts`)
  copies the injury onto every outer ancestor of an inside part it hits. The
  importer weighs the body's parts by `coverageAbs` x hit chance factor and
  gives each shot an extra-injury count drawn from that spread
  (`CombatSpec.HitLayers`). Ignored: part health caps and destruction, overkill
  redistribution, `ReduceDamageToPreserveOutsideParts`, per-part pain factors;
  the shot's height and depth are not modeled (a bullet has none). Armor stays
  part-agnostic (A5).
- **A35. Pawns pass through and share cells; only a standing hostile blocks.**
  Checked against the pinned assembly (`Pawn_PathFollower.PatherTick`,
  `WillCollideWithPawnAt`, `PawnUtility.ShouldCollideWithPawns`,
  `PawnBlockedBy`, `PawnsCanShareCellBecauseOfBodySize`, `PathGridDoorsBlockedJob`,
  `PathFinderJob` Cost_Blocked, `PawnCollisionTweenerUtility`), not assumed:
  - The pather refuses to enter a next cell only when `PawnBlockedBy(self,
    occupant)` holds. Both must pass `ShouldCollideWithPawns` (not downed, dead
    or self-shutdown; `mindState.anyCloseHostilesRecently`, refreshed from
    `EnemiesAreNearby` over 18-24 regions; `kindDef.collidesWithPawns`, default
    true). Then the occupant blocks if it `HostileTo` the walker or, for a
    friendly, only if both pawns' current jobs set `collideWithPawns` (in Core:
    AttackStatic, AttackMelee, the Cast* abilities, Ignite; never Goto or
    Wait_Combat). A walking colonist on a Goto is therefore never held by a
    colonist, and pawns of one side may stand on one cell: `Goto` reserves its
    destination but does not require it free, and the native bridge sends the
    exact cell (`RCellFinder.BestOrderedGotoDestNear`, which does pick a free
    nearby cell, is only the player UI's order path).
  - Pawns under 1.5 body size whose sizes differ by over 3.57x never block each
    other (`PawnsCanShareCellBecauseOfBodySize`).
  - `PawnCollisionTweenerUtility` is only a draw offset (0.32 cell on a regular
    polygon among the pawns on or heading to the cell); it moves nothing.
  - The pathfinder has no hard pawn rule: `PathGridDoorsBlockedJob` marks the
    cell of every pawn that `PawnBlockedBy` would block (within 32 cells
    Manhattan of the path's start or destination) and `PathFinderJob` adds
    `Cost_Blocked` = 175 to entering it, so a path goes round a hostile when the
    detour costs less than 175 more.
  - Blocked, the pawn pays no movement cost (the collide check precedes the cost
    payment). `NeedNewPath` (every 30 ticks) re-paths from the pawn's cell when a
    cell within 20 nodes collides. With fewer than 15 nodes left, a second tick
    held by the same hostile, or 180 ticks without moving, ends the walk by
    starting an `AttackMelee` on the blocker (if it can) or `EndCurrentJob`.
  - A standing pawn that `PawnBlockedBy` says collides with another pawn on its
    own cell, and has the higher thing id, is moved by `PatherTick`
    (`CellFinder.TryFindBestPawnStandCell`, Dijkstra over cardinal steps, a
    teleport to the nearest free cell). That needs a hostile on the cell or two
    friendlies whose jobs both set `collideWithPawns` (both shooting on
    AttackStatic), so walkers and a resting pair never trigger it.

  The sim (`collision.go`): a per-cell list in entry order (the thing list;
  every pawn on a shot's cell gets its intercept roll). `blockerAt` returns a
  live, up pawn of another side on the cell (hostile = the sides differ; with
  both alive, "a hostile is near" holds for each, so `anyCloseHostilesRecently`
  is not tracked: its 100-tick refresh lag and region radius are ignored), minus
  the body-size exemption. A friendly never blocks. A downed pawn neither blocks
  nor stops others (A4). A blocked pawn pays nothing, re-paths every 30 ticks
  (phase = tick + pawn index; the game's is the thing id), and ends the walk
  (`EventStopped`) on the same-blocker second tick under 15 nodes or after 180
  ticks without moving; the melee swing is not modeled (no melee, E8). The
  pathfinder prices a blocker's cell +175 within 32 cells. Not modeled: the
  stacked-shooter relocation above (it needs two colonists firing from one cell,
  which only an order stacking them produces; the sim lets them fire together),
  thing-id tie-breaks, and `lastBlocker` bookkeeping beyond the second-tick rule.
  `Autonomy` now reads the pawns after the policy's orders applied
  (`Sim.Apply`), as a Goto replacing Wait_Combat does; before, a walker was
  seen as idle and also opened fire.

  Effect on lab-ranged (#2909, 20 seeds, default tactic; before -> after, real):
  colonist openings (median) 61/61/61/61 -> 61/97/97/127 (60/91/91/121),
  first-700-tick colonist hits 17.95 -> 17.70 (16.0), median raider down tick
  478 -> 514 (499.5), colonist downs 0.20 -> 0.20 (0.20), damage 56.6 -> 58.3
  (64.2), raider deaths 3.40 -> 3.65 (about 2.8). The movers opened 6 ticks
  late (97 against 91, 127 against 121) while the lab stepped 6 ticks:
  `Autonomy` ran once per step, so a walker that arrived at tick 31 shot from
  the next boundary (36). The lab now steps one tick (#2910), so `Autonomy`
  and the raiders run every tick: openings 61/92/92/122 (real 60/91/91/121),
  first-700 hits 17.65, median raider down tick 510, damage 56.1, raider deaths
  3.55, colonist downs 0.20. The remaining 1 tick was the sim starting a
  Wait_Combat attack a tick late (fixed in #2914, below). Nothing in the
  adapter, the enemy AI or movement counts steps: every cadence, timer and
  expiry is in ticks. Cost: the 4v4 and 12v12 core benchmarks run 2.5-3.5x
  slower per fight (their controller runs every step), a full lab-ranged trial
  (policy and raiders included) about 14% slower.

- **A36. When a job's verb starts (#2914).** `TickManager.DoSingleTick`
  increments the tick, then ticks the things; `Pawn.Tick` runs the pather, the
  verbs, the stances, the job driver, then the equipment. A job started by
  `Pawn_JobTracker.StartJob` runs its first toil's `initAction` inside StartJob,
  on the tick that starts it (an order issued at a stop, the clock paused, is
  tick N; a `Goto` that ends in the pather ends there and the next job starts in
  the same call). `Wait_Combat`'s `initAction` calls `CheckForAutoAttack`, which
  calls `Pawn.TryStartAttack` -> `Verb.TryStartCastOn`, setting the
  `Stance_Warmup` at once; `Stance_Busy.StanceTick` first decrements it on tick
  N+1 and expires it at N + warmup, where `WarmupComplete` fires the first
  shot. A `JobDriver_AttackStatic` starts the verb in its toil's tick action,
  one tick after the job starts, so a forced attack opens at N + 1 + warmup. The
  sim modeled only the second; `Order.Auto` now marks an attack as the first
  (the colonists' `Autonomy` and the raiders' `autoAttack`): the verb starts the
  tick the order lands. A burst's cooldown ending already restarted the next
  warmup in the same tick (stances run before jobs), so only the entry into a
  fight moved.
  Raiders: a raider's decision at tick 0 is a resumed checkpoint's pawn that
  is already in `Wait_Combat`: the recorded stance rows show warmup "left 60"
  at tick 0 and all 80 raiders' first shot on tick 60, so `Auto` holds at tick
  0 too (#2933; this replaced the 61 of #2914). A mover that arrives starts the
  same auto-attack inside the pather tick that ends its walk (the pather runs
  before the stance tick), so that tick's stance tick already counts the
  warmup down: the recorded movers arrive 29/29/57 and shoot at 88/88/116,
  arrival + 59 (`Sim.onArrive`, the adapter's `arrived`). Later restarts (a
  cooldown ending, a job starting) are one tick earlier than before.
  Not modeled: the game ticks a pawn's job driver (`tickIntervalAction`, which
  `CheckForAutoAttack`'s 4-tick re-scan and `AttackStatic`'s verb start use) and
  its job-tracker think from `Pawn.TickInterval`, which `Thing.DoTick` calls
  every `GenTicks.GetCameraUpdateRate` ticks (15 for a pawn the camera does not
  see, as in a headless run) at a phase set by the thing's hash. A raider's first
  think, an AttackStatic start and the re-scan can therefore land up to 14 ticks
  after the sim's. The colonist openings above do not depend on it (they come
  from `initAction`, and the pather ticks every tick).
  Effect on lab-ranged (20 seeds, default tactic; before -> after, real):
  colonist openings 61/92/92/122 -> 60/91/91/121 (60/91/91/121), first-700-tick
  colonist hits 17.65 -> 17.85 (16.0), median raider down tick 510 -> 513.5
  (499.5), colonist downs 0.20 -> 0.20 (0.20), damage 56.1 -> 55.5 (64.2), raider
  deaths 3.55 -> 3.45 (about 2.8). The openings now match; the remaining gap in
  hits, damage and deaths is not the opening tick.

- **A37. A path request is claimed a tick late (#2933).**
  `Pawn_PathFollower.StartPath` queues the request on tick N; `PatherTick`
  claims the result of the async `PathFinder` job on N + 1 and pays the first
  cell's cost from N + 2. The sim's walk waits one tick before it pays
  (`pathLatency`, also in the planned arrival). With a speed of 5.0 (a cost of
  14.0 per cell on the lab's soil) the recorded movers arrive at 29/29/57,
  where the sim without the tick arrived at 28/28/56.
  The think phase (every pawn's `JobTrackerTickInterval` is on the same
  15-tick phase as its projectiles' hash rule) is not modeled. It was checked
  on 163 recorded pawns and fits, but in the steady cycle every pawn restarts
  its warmup the tick its cooldown ends (the between-burst gap is exactly 162
  for 90 percent of the gaps in both), so it quantises nothing observable
  there. It would delay a colonist's retarget by up to 14 ticks, which shows
  in the recorded 171, 185, 191 and 238 to 302 tick gaps (about 1 percent of
  gaps); the sim has none of them (its 99th percentile is 162, the game's
  288).
- **A38. Traits that move a stat are read (#2933).** The lab crew's
  superpawn traits (`SpeedOffset` degree 2, `Industriousness` degree 2,
  `na.SuperTraits`) are applied after the staging; `SpeedOffset:2` is +0.4
  `MoveSpeed`, so the recorded colonists walk at 5.0, not the 4.6 of a
  traitless adult. The importer reads a pawn's `Traits` through the stat
  engine (`stateval`). Why the *fresh* stage gives arrivals 32/32/62 (a 4.6
  walker) is not explained: the stats are recomputed on every read, so a stale
  cache is not it. A real fresh-stage run that logs the colonists' traits and
  `MoveSpeed` at tick 0 would settle it; the gate's baselines are all resumed
  runs and use 5.0.

## Out of scope for this slice

Melee, explosives and grenades, specialist abilities, breaches, turrets,
mechs, sieges, drop pods, non-ranged raider behavior, downed/carried movement,
doors closing, repathing other than round a blocking hostile (A35), fire, snow and sand path costs (see the package
comment).
