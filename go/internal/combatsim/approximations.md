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
  capacities, pain and blood loss. No hediffs, no body parts.
- **A2. No bleeding, pain, infection or recovery.** Damage never decays and
  nothing is tended; a downed pawn does not bleed out.
- **A3. Damage is applied whole.** The game splits a hit across body parts and
  scatters a share to neighbours; here the full post-armor amount counts once.
- **A4. A downed pawn stays where it fell** and is not carried, crawling or
  rescued. A dead pawn frees its cell; a downed one still occupies it.

## Armor

- **A5. Armor is body-part-agnostic.** `ArmorUtility.GetPostArmorDamage` tests
  each apparel's `CoversBodyPart` for the part hit; here the caller flattens the
  pawn's layers into one ordered list of ratings (outermost first, natural last).
- **A6. No deflect-to-blunt, no apparel damage.** A halved sharp hit stays a
  damage amount; the 25% wear on the apparel is skipped (and with it the RNG
  draw the game spends there). Only the stop/halve/pass roll is ported.

## Hit resolution

- **A7. Wild and cover misses hit nothing.** The game sends a wild shot to a
  random cell (it can strike a bystander) and a covered shot into the cover
  thing; both here end with no damage and no friendly fire. The wild-miss
  destination (`ChangeDestToMissWild`) and the cover thing choice
  (`GetRandomCoverToMissInto`) are not ported.
- **A8. The line is cell centre to cell centre.** Projectiles leave the
  shooter's cell centre (not the lean cell) and aim at the target's cell centre
  (the game adds `RandomHorizontalVector(0.3)` to the destination).
- **A9. Interception is decided at launch.** The flight is walked once, tick by
  tick, against the pawns standing at the moment of the shot; a pawn that steps
  into or out of the line while the projectile flies is not seen. The victim and
  landing tick are fixed then. Damage is applied at the landing tick.
- **A10. Only pawns intercept.** A shot aimed at a pawn carries the
  IntendedTarget and NonTargetPawns hit flags only, so walls, doors and cover
  never intercept in flight; they matter through line of fire (`CellCanSeeCell`)
  and the cover roll. This is the game's rule, noted because it surprises.
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
- **A21. A holder shoots on its own.** `hold_position` is Wait_Combat in the game,
  whose auto-attack picks a hostile in reach. `Autonomy` gives an idle holder
  that is not on hold fire an attack on the nearest hostile it can hit (ties to
  the earlier scenario pawn). The game's target scoring is not ported.
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
- **A31. Lab assumptions are caller-stated.** Skills 8, an assault lord toil
  for hostiles, hostile apparel destroyed, colonist apparel and the difficulty
  friendly-fire factor are inputs (`combatlab.SimEnv`), not readings; the last
  two stay unknown, and the import fails, until stated.

## Out of scope for this slice

Melee, explosives and grenades, specialist abilities, breaches, turrets,
mechs, enemy target choice, downed/carried movement, doors closing, repathing,
fire, snow and sand path costs (see the package comment).
