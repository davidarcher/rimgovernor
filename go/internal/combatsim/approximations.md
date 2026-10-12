# combatsim approximations

Every place the ranged-combat port (`combat.go`, `shot.go`) departs from the
game, and the labeled simplification that stands in. A departure not listed
here is a bug. The calibration gate (#2844) decides which of these matter; each
is a candidate to replace by a faithful port. Game sources are in
`rules_version.go`.

## Injury and health

- **A1. Injuries are per body part (A39).** Each pawn carries the injuries on
  the parts of its `CombatSpec.Body`, ported from `Pawn_HealthTracker`,
  `HediffSet` and the capacity workers: pain shock, the consciousness, Moving
  and Manipulation capacities, vital-part loss, core-part destruction and the
  lethal damage threshold decide when a pawn is downed or dead. What is left
  out of that model is listed in A39. A hostile that becomes downable also
  takes the death-on-downed roll (A33).
- **A2. Blood loss is ported (#3000); infection, healing and tending are not.**
  Every 60 ticks on the pawn's hash phase `HediffGiver_Bleeding` grows the
  BloodLoss severity by bleed rate x 0.001 (bleed rate at least 0.1, else it
  falls by 0.00033), through the stages 0.15/0.30/0.45/0.60 with their
  Consciousness offsets (-0.1, -0.2, -0.4, -0.4, the last also capped at 0.1),
  downing on the stage change and killing at severity 1 (no death-on-downed
  roll: the state change carries no damage info). Injuries never decay, heal or
  get tended. Measured on lab-split-ranged (200 seeds) it moves nothing to two
  decimals: bleeding only builds on pawns already downed, after the fight.
  Tending is skipped on evidence: the real seed-1 run's `TendPatient` jobs are
  each followed by a policy `Goto` on the same tick, so no colonist leaves the
  line to tend. Mental breaks are skipped: the log records one `mental_break`
  stop with no pawn, state or mood data to source a rule from.
- **A3. Damage lands on one part, plus the outer-part duplicates of A34.** The
  game's shot-height/depth choice does not exist for a projectile hit, so the
  part is drawn by `coverageAbs` x hit chance factor (A39); there is no
  scatter of a share to neighbours.
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
  lost (`stopBurstWithoutLos`) and the cooldown starts. A raider's self-started
  attack (E2) is not retried: `Stance_Warmup.StanceTick` drops the warmup to
  `Stance_Mobile` when the target can no longer be hit, `TryStartAttack` keeps
  no order, and `Notify_StanceChanged` runs `CheckForAutoAttack`, so the raider
  scans again at once and aims at whatever it can still hit (#3001). Before
  that the sim kept the order and the raider stared at a colonist that had
  walked out of reach, never scanning, until its 450-550 tick wait ended.
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
- **A22. Three stop kinds are raised.** `downed` (a colonist downed or
  killed), `entered_range` (a hostile came within a colonist's weapon range,
  or a colonist's weapon reached it; distance only, no sight, once per hostile
  each way, the first scan recording the start) and `serious_injury` for one
  hit of at least `policy.SeriousSingleHitDamage` (20), summary health
  crossing to or under 0.5 (`Verse.SummaryHealthHandler`: product over parts
  of 1 - min(impact, 0.95), impact = severity / (75 x HealthScale), a lost
  part by its hit points) or the total bleed rate crossing to or over 1.0
  (`Hediff_Injury.BleedRate`: severity x injury x race x part bleed rates, 0 on
  solid parts, over HealthScale; #2991), on a colonist (A39). The vital-part
  trigger is not raised because it cannot fire for bullets:
  `Bullet.Impact` passes a null hit part and `Apply` works on a by-value copy,
  so `dinfo.HitPart` is null in `PostApplyDamage`. The shield, lord, melee,
  explosive and mental-state stops are not raised: the sim has none of those
  things. A backstop (300 ticks, the native one, #2994) asks the
  policy again. Injuries merge only where the injury def has `canMerge`
  (not for bullets), so each hit is its own injury for both sums.
  Measured on lab-split-ranged over 200 seeds, the two ported triggers moved
  colonists' wins 0.545 -> 0.480 (real 0.25) and downs+deaths 2.52 -> 2.72
  (real >= 3.35); the remaining gap is not the stop triggers.
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
  each Step. A job that ends mid-Step is replaced at the next boundary. A job
  the pawn does not end itself is only seen on the pawn's 15-tick tracker tick,
  and the auto-attack search runs at job start, when a burst's cooldown ends
  (`Notify_StanceChanged`) and on that tick (A41). With `StepTicks` 1 (the
  lab's) there is no other difference; a larger step delays by up to one step.
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
  `IsHashIntervalTick` alignment is ported (A41); `expireRequiresEnemiesNearby`, damage-driven
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
- **E9. The faction's panic-flee transition is ported; its fraction is stated.**
  `Lord.SetJob` adds a `LordToil_PanicFlee` transition to every lord of a
  non-player faction with `autoFlee` (the default), unless the lord job opts out
  (`AddFleeToil`) or `neverFlee`: it fires on `Trigger_FractionPawnsLost`
  when `numPawnsLostViolently >= numPawnsEverGained x fraction`, the fraction
  drawn from `attackersDownPercentageRangeForAutoFlee` (0.4 to 0.7) seeded by
  the lord's load id. `Lord.Notify_PawnLost` counts `Incapped` and `Killed`
  (a downed raider that dies later left the lord already, so it counts once).
  The lab stages the assault lord with `canTimeoutOrFlee: false`, which only
  removes E7's two transitions; this one stays, and the real baselines show it
  (lab-split-ranged: 3 raiders flee once 5 of 8 are lost; lab-ranged: the last
  raider flees at the third loss, and its `Goto` to the map edge is in the shot
  dataset). `EnemyConfig.AutoFleeFraction` states the fraction (unknown: no
  panic; needs `Exit`); `EnemyAI.Observe` runs the trigger after each Step and
  every raider still standing stops fighting and walks to `Exit` as in E7. The
  runner stays on the map, so colonists can still down it, and it counts as
  fled only if it leaves the map. The lab's value is 0.5625, the midpoint of
  (0.5, 0.625] that both baselines bracket (`calibrationAutoFleeFraction`);
  the lord load ids are in no committed file. Not modeled:
  `MentalState_PanicFlee`'s own job choice (`JobGiver_ExitMapPanic` picks the
  best exit, here the one stated `Exit`) and its run speed.

## Scenario importer (`simimport/`)

The importer derives what the sim holds no literals for from the native
DefinitionCatalog; every derived value is listed with its source in
`Result.Trace`. A value it cannot derive fails the import with `ErrUnsupported`.

- **A24. A melee weapon imports as unarmed.** The pawn keeps its catalog melee
  DPS as `MeleePower` and is named in `Result.Unmodeled`; no melee verb is
  simulated. Explosive, launcher, one-use, EMP and incendiary weapons, turrets
  and non-humanlike races are refused, as is a spec with no ranged weapon at all.
- **A25. The body is derived from the catalog, for one damage def.** The
  importer builds `CombatSpec.Body` from the race's `BodyDef` (parts in
  pre-order, `coverageAbs`, depth, `hitPoints` x HealthScale rounded up, solid,
  destroyableByDamage, the capacity tags and limbs), the damage def
  (`harmAllLayersUntilOutside`, `overkillPctToDestroyPart`, hit chance factors),
  the injury and `MissingBodyPart` hediffs' `painPerSeverity`, the
  `PainShockThreshold` and `IncomingDamageFactor` stats and
  `BaseTotalDamageLethalThreshold` x HealthScale. It refuses: an injury hediff
  with stages, a lethal severity or a painless chance; a pawn kind that ignores
  pain shock; a capacity worker other than the ported one or a lethal-flesh
  capacity the sim does not evaluate; and a spec with a colonist whose
  difficulty `allowInstantKillChance` is unstated or below 1 (A39). A spec may
  use one damage def only.
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
  `forceDeathOnDowned` and `overrideDeathOnDownedChance` overrides are read;
  animals and blood loss deaths are not modeled. Vital-part loss, core-part
  destruction and the lethal damage threshold kill outright (A39).
- **A34. Hit layers duplicate injuries to outer parts.** A bullet's
  `harmAllLayersUntilOutside` (`DamageWorker_AddInjury.CheckDuplicateDamageToOuterParts`)
  copies the injury onto each ancestor of an inside part it hits, up to and
  including the first outside one that still has health and coverage. Now
  ported per part (A39); the shot's height and depth are not modeled (a bullet
  has none). Armor stays part-agnostic (A5).
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
- **A39. The single-hit serious-injury stop is raised (#2985).** The sim
  colonists killed far more raiders per colonist downed than the real ones on
  lab-split-ranged. The game's `SupervisedPlayCombatStops` hook on
  `Pawn_HealthTracker.PostApplyDamage` stops the clock on a hit of at least 20
  damage (41 percent of the colonist hits in the ledger are 22); the policy's
  `fallBack` then orders the hurt defender to its Retreat cell, and since a
  move order does not cancel a running cooldown (A36) the walk waits out the
  rest of the shot cycle: 80 to 240 ticks of firing lost per such hit. The sim
  raised only `downed` and `entered_range`, so its colonists never retreated.
  Order latency (backstop, cooldown-blocked moves) was already modeled and
  the backstop is not the cause (120, 300 and 600 move nothing). Lab-split-ranged
  over the 20 baseline seeds (real, sim before, sim after): colonist wins
  5, 14, 14; colonist downs 2.95, 2.15, 2.10; colonist damage 262, 213, 207;
  raiders lost 3.75, 4.40 (6.3 before E9), 4.40. Scatter-short vs default
  downs (the #2982 separation): real 2.25 vs 2.95; sim 2.30 vs 2.10 (before
  2.15-2.20 for every variant, now spread 2.10 to 2.65, but scatter-short
  still points the other way). Not modeled and still open: the summary-health,
  bleed and vital-part serious triggers (more stops, so probably more of the
  same effect), the 15-tick think phase, and a real retreat that lands 40 to
  56 ticks later than the sim's after the cooldown.

- **A39. The per-part injury model (#2986).** `Body` (`body.go`) holds the
  parts the importer derived (A25) and each pawn's injuries. A projectile hit
  draws a part (`HediffSet.GetRandomNotMissingPart`: weight `coverageAbs` x the
  damage def's hit chance factor, conceptual parts none), applies armor (A5) and
  `IncomingDamageFactor`, then an outside non-core part that would take more
  than its health is left at 1 unless the overkill roll destroys it
  (`ReduceDamageToPreserveOutsideParts`, range 0..0.7), and the injury is added
  with the inside-part duplicates (A34). A part past its health that is not the
  core goes missing with everything under it, its injuries removed (`Hediff_
  MissingPart.PostAdd`); an outside, non-solid part lost this way hurts by its
  maximum health x the missing-part pain rate. After each injury
  `CheckForStateChange` runs: dead on core-part efficiency 0, a lethal capacity
  (Consciousness, BloodPumping, Breathing, BloodFiltration, Metabolism) lost,
  or injuries summing to 150 x HealthScale; else downed on pain at the
  `PainShockThreshold`, Consciousness under 0.3 (which also zeroes Moving and
  Manipulation, `zeroIfCannotBeAwake`) or Moving at most 0.15 (the first such
  injury rolls the death-on-downed chance, A33); else a pawn whose Manipulation
  reached 0 drops its weapon and stops shooting. `Event.Amount` and the ledger's
  `Dealt` are the damage actually dealt, `min(severity, part health)`
  (`totalDamageDealt`), which is what the real ledger records. The RNG order per
  hit is the part draw, the armor rolls, the overkill roll, the death roll.
  Left out: bleeding and blood loss (A2); infection, tending and recovery; the
  hediffs' stage capacity modifiers (the importer refuses a staged injury);
  part-specific armor (A5); the move-speed loss from pain or a damaged leg
  (only the Moving capacity's down threshold is read); a pawn that has dropped
  its weapon does not take up melee; instant-kill protection for colonists
  (`allowInstantKillChance`, which the lab leaves at 1, so none applies); added parts and other
  hediffs; sight, hearing and other capacities no rule here reads.
  Calibration (lab-ranged, 20 resumed seeds, after the E10 serious-injury stop
  merged): colonist shots per pawn 11.71 -> 11.25 (real 10.24), raiders 9.75 ->
  9.45 (real 8.84); the median raider down tick 521 -> 535.5 (real 499 to 504).
  The raiders now go down on the same cumulative damage as the real ledger (66,
  the 64 pain-shock line crossed by whole 11-point hits), so the rest of the gap
  is hit timing and volume, not the health model. The #2986 target of 10.24 /
  8.84 / 499 was not reached: shots per pawn improved, the down tick did not.
  Colonist deaths (#2989): the "0 real deaths" was a measurement gap, not a
  property of the game. The decompile finds no colonist protection in the
  damage path: `FinalizeAndAddInjury` softens a would-be instant kill only when
  `!Rand.Chance(allowInstantKillChance)`, and the difficulty's value is 1 (the
  `Difficulty.CopyFrom` default; no `DifficultyDef` sets it), so a colonist's
  brain, heart or core part is destroyed exactly as a raider's is, and
  `CheckForStateChange` has no colonist branch except that the death-on-downed
  roll skips the player faction. The sim's deaths on lab-split-ranged are the
  rules at work: an 11-point hit on the 10-health brain (5 of the 8 deaths over
  20 seeds) and stray hits that destroy the 40-health torso of a colonist
  already down (3). The real sweep scores a colonist from the final read, and
  `MapPawns.AllPawns` omits the dead, so a dead colonist counted as neither dead
  nor down (only hostiles were recovered from the ledger). The baselines show
  it: the raiders won 15 of the 20 lab-split-ranged seeds, which needs all four
  colonists out, yet the downs are fewer than four in 7 of them (3 in six, 2 in
  one), so the real runs lost at least 8 colonists in 20 seeds, 0.40 per seed,
  the sim's 0.40 (the 5 colonist-win seeds, and lab-ranged, cannot be bounded).
  `combatmetrics.Aggregate` now takes colonist deaths from the ledger's `dead`
  hits; the committed sweeps stay as recorded (the calibration report says so)
  and the next recording counts them. What remains of the downs gap is the
  length of the fight, not injuries: the sim's colonists win 14 of 20 seeds
  against 5 real, so fewer of them go out (sim 2.25 downs plus deaths per seed
  against at least 3.35 real).

- **A40. Cover is computed in float (#2994).** The game's
  `CalculateOverallBlockChance` and `TryFindAdjustedCoverInCell` are float
  arithmetic and the native geometry reply widens the float to a double.
  `policy.CoverScore` sums those over the hostiles, and `RankByCover` keeps
  the firing-cell order on equal scores. Float values sum exactly in a double;
  the sim's double cover values did not, so four firing cells with the same
  cover (lab-split-ranged: four chunks in a row) ranked by rounding noise and
  the formation sent every colonist to a neighbour's cell at tick 0. The real
  service (seed 1 of the #2994 run) orders `hold_position` for all four, the
  first colonist shots land at tick 60 and not 88, and the colonists keep the
  line a walk longer. Same seeds 200, sim only, before (backstop 120, double
  cover) and after: colonist wins 0.48 -> 0.65, downs plus deaths 2.72 -> 2.08,
  raiders lost 5.39 -> 6.29, fight length (contact to resolution) 2434 ticks
  against 5189 real. The fix removes a false walk and moves the sim away from
  the real aggregate: the first timing divergence is gone and a larger one
  (the real fights run twice as long, with 2.95 downs and 262 damage against
  2.08 and 206) is what remains. The default backstop is now the native 300
  (A22); it moves nothing measurable (0.675 -> 0.650 wins).
  Rescue: the policy emits a `rescue` order only for a route clear of hostile
  line of fire, or with no hostile left; the sim refuses it `native_refused`
  (no carry, no tend), but no lab-split-ranged run issues one while hostiles
  stand, and the real run issued none either (its `TendPatient` jobs are the
  separate tend planner's, forced between combat stops).

- **A41. Jobs are seen on a 15-tick think phase (#3000).** The game's
  `Pawn_JobTracker.JobTrackerTickInterval(15)` runs for a pawn when
  `(tick + HashOffset(thingIDNumber)) % 15 == 0`; the recorded phases of twelve
  lab pawns match `thingNumber` and `hashTick`
  (`TestPawnThinkPhaseMatchesRecordedTicks`). Consequences ported: a job's
  expiry (`startTick + interval <= now && IsHashIntervalTick(interval, 15)`)
  fires only on a call tick inside the window that opens once per interval, so
  a 450-550 tick Wait_Combat lasts up to 1.5x as long (two recorded raider
  jobs reproduce, `TestWaitCombatExpiryWindowReproducesRecordedEnds`); a null
  job restarts on the next call tick; `CheckForAutoAttack` runs at job start,
  on a stance change and on each call tick, not every four ticks. A colonist's
  `Autonomy` search follows the same rule (on becoming free, or a call tick);
  a finished walk still searches at once (A36). Same 200 seeds, sim only,
  before and after both cadence ports: colonist wins 0.65 -> 0.72, downs plus
  deaths 2.08 -> 1.79, damage 206 -> 195, contact to resolution 2434 -> 2436.
  This is faithful and moves the headline away from the real 0.25 wins, 262
  damage: the cause of the colonists' sim advantage is not the cadence. The
  real contact-to-resolution is mostly a measurement artefact:
  `combatmetrics.decisiveHit` returns the harness read tick when the loser is
  absent from the read (a dead colonist, a fled raider), so the real 5189
  averages exact ends (all four downed, 2832 real against 2810 sim) with read
  ticks (a colonist dead, 8540 real against 2725 sim; colonist wins, 4270
  against 2304).

- **A42. A dropped raider warmup rescans (#3001).** The stale-order bug above
  was the main cause of the sim's raiders firing too little on lab-split-ranged
  (colonists step back from 40 to 35 cells at about tick 110, which carries
  them out of the rifle's 30.9 cells from the raiders' rows; the real raiders
  keep firing at the colonist left in reach, the sim's stood silent for
  a wait). Same 200 seeds, sim only, before -> after (real in brackets; the
  real shot ledger is the one committed seed-1 run, 129 hostile shots, 12 on
  pawns): hostile shots 91.0 -> 116.6 (129), hostile pawn-hit share 0.143 ->
  0.138 (0.093), colonist shots 55.5 -> 53.6 (64) at 0.423 -> 0.428 (0.42),
  colonist wins 0.72 -> 0.52 (0.25), downs plus deaths 1.79 -> 2.80 (2.95
  downs, deaths read as 0), damage 195 -> 239 (262). The raiders' hit share
  stays above the real one, on lab-ranged as well (0.122 sim, 0.099 real over
  707 shots): the real raiders' shots end in cover more (0.378 against 0.357)
  and aim at the target cell only 61 percent of the time (colonists 96).

- **A43. A shooter's accuracy follows its injuries (#3006).** The real
  raiders aimed worse than the sim's and the cause was not the shot report but
  the stat it reads: `ShootingAccuracyPawn` is recomputed on every read as the
  skill term plus `PawnCapacityOffset`s (Sight scale 12 max 2, Manipulation
  scale 8 max 1, `(min(level, max) - 1) x scale`) through the stat's
  postProcessCurve, so a raider hit in the arm or the eye, or in pain, shoots
  worse the next burst. The sim read the stat once at full health. It now
  inverts the curve for the read value, adds the offsets of the pawn's current
  Manipulation (limb damage times Consciousness, which pain and blood loss
  lower) and Sight (`PawnCapacityWorker_Sight`: eyes, best eye weighted 0.75),
  and re-evaluates the curve after every hit and blood-loss stage. The curve
  and the two offsets are Core XML read by hand (the assembly hash covers the
  workers, not the defs); trait, gear and hediff stat offsets stay folded into
  the read value, and no hediff moves Sight or Manipulation here (A39). Real
  evidence (the committed lab-ranged shot ledger, raiders' shots by the
  damage the shooter had taken: share of shots ending 2 or more cells from the
  target cell 0.26, 0.31, 0.49, 0.55, 0.65 for 0, 1..11, 12..22, 23..33, 34+
  damage; colonists with skill 20 barely move, 0.03 to 0.12). Share of
  raiders' shots landing within one cell of the target, sim before -> after
  (real in brackets): lab-ranged 0.739 -> 0.601 (0.609), lab-split-ranged
  0.602 -> 0.561; colonists 0.967 -> 0.959 (0.957). Sim only, 300 seeds:
  lab-ranged colonist damage 74.9 -> 68.6 (69.0), lab-split-ranged raiders
  lost 5 or more in 0.53 -> 0.52 of runs (real 0.25) and colonist wins 0.53 ->
  0.52 (0.25): this was not the cause of those. The raiders' pawn-hit share
  stays near 0.124 (lab-ranged, real 0.099) because the aimed-and-uncovered
  shots that carry it (cell offset 0) are 0.10 of raider shots against the
  real 0.079, and the raiders' shots stopping in a sandbag fall from 0.357 to
  0.308 (real 0.378): the same wild destinations hit a sandbag less often in
  the sim than in the ledger (destination cell beside the target on its own
  row: 0.33 against 0.52 of 60 to 70 shots; the cause is A44).

- **A44. A resumed checkpoint's raiders start aimed (#3007).** The wild
  shots beside a target hit a sandbag less often in the sim only because the
  sim's raiders chose different targets, not because a wild shot, the
  intercept or the impact differs: those were checked line by line against the
  decompile (`Verb_LaunchProjectile.TryCastShot`, `Projectile.CheckForFreeIntercept`,
  `ImpactSomething`) and the per-target stop rates match the ledger. The lab
  runs resume a saved checkpoint in which each raider is already in
  `Stance_Warmup` on a target. The pawn is FullBodyBusy, so
  `CheckForAutoAttack` does not pick again, and `Pawn_MindState
  .lastAttackedTarget` (+40 in `GetShootingTargetScore`) keeps it afterwards.
  The ledger's first targets are therefore fixed per checkpoint (seeds 1 to 13
  one set, 15 to 20 another) while the sim drew from near-equal scores
  uniformly, aiming more at the east end colonist, whose neighbouring wild
  cells lie beyond the sandbag row. `EnemyConfig.FirstTargets` names a
  raider's staged target; its tick-0 decision takes it when it is a valid
  candidate. Empty: every raider draws (the other cases, where no ledger
  exists). The calibration test reads the targets from the ledger per seed.
  Raiders' shots stopping in a sandbag, sim before -> after (real 0.378):
  0.308 -> 0.362; pawn-hit share 0.124 -> 0.090 (real 0.099). Left: the wild
  shots on the target's row still stop in a sandbag in about 0.40 of the sim
  against 0.52 (about 2 sigma of 69 shots).

- **A45. lab-split-ranged's staged targets come from its checkpoint save
  (#3011).** The staged `Stance_Warmup` is in the checkpoint save itself
  (`RimGovernor-checkpoint-combatlab-metrics-<fixture>.rws`, `curStance
  Class="Stance_Warmup"`, `focusTarg`), so no new run is needed to learn a
  fixture's targets, only a staged save of it. lab-split-ranged's save (staged
  digest ca770629, the digest of all 20 baseline rows, so every baseline seed
  resumes it and the reseed leaves the stance alone) has its eight raiders in
  thing id order aimed at colonists 0 1 1 2 1 2 0 1; the seed-1 shot ledger's
  first shots agree. They are a committed table (`combatlab.stagedFirstTargetTable`,
  keyed by the digest, which a test checks against the baseline) that
  `SimSpec` hands to `simimport.Raid.FirstTargets`, so the calibration and the
  gate sweeps use it. lab-ranged keeps the ledger-fed per-seed targets (its
  ledger shows two sets, seeds 1 to 13 and 14 to 20; no save of it was
  available to say which the baseline resumes). Fixtures with no staged save to
  read keep drawing. Sim only, 200 seeds, lab-split-ranged, before -> after
  (real, 20 seeds): colonist wins 0.53 -> 0.435 (0.25), raiders downed or
  killed 5 or more 0.53 -> 0.435 (0.25), colonist downs plus deaths 2.74 ->
  3.00 (2.95), colonist damage 236.6 -> 247.4 (262.3), raiders' shots stopping
  in cover 0.147 -> 0.143 and hitting a pawn 0.131 (seed 1 ledger: 0.155,
  0.093 of 129 shots). Left: the win rate is 1.9 sigma of 20 real seeds above
  the real; the aimed-and-uncovered share of lab-ranged the A43 note called
  high is 0.090 against 0.099 after A44, so no gap remains there.

## Out of scope for this slice

Melee, explosives and grenades, specialist abilities, breaches, turrets,
mechs, sieges, drop pods, non-ranged raider behavior, downed/carried movement,
doors closing, repathing other than round a blocking hostile (A35), fire, snow and sand path costs (see the package
comment).
