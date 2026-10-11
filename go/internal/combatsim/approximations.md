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

## Out of scope for this slice

Melee, explosives and grenades, specialist abilities, breaches, turrets,
mechs, enemy target choice, downed/carried movement, doors closing, repathing,
fire, snow and sand path costs (see the package comment).
