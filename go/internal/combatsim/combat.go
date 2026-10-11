package combatsim

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// WeaponDef is a ranged weapon's verb as the game reads it for one pawn: the
// catalog values after the weapon's own stat multipliers (range, warmup,
// cooldown and accuracy are the effective numbers, not the raw def fields).
// The package holds no weapon values; a zero field that the rules need
// positive fails Reset with ErrUnsupported.
type WeaponDef struct {
	Name string
	// Range is Verb.EffectiveRange and MinRange VerbProperties.EffectiveMinRange,
	// both in cells.
	Range, MinRange float64
	// WarmupSeconds is Verb.WarmupTime; CooldownSeconds is the weapon's
	// RangedWeapon_Cooldown stat (VerbProperties.AdjustedCooldown before the
	// shooter's factor).
	WarmupSeconds, CooldownSeconds float64
	// BurstShotCount is Verb.BurstShotCount and TicksBetweenBurstShots the
	// verb's ticksBetweenBurstShots.
	BurstShotCount, TicksBetweenBurstShots int
	// Accuracy* are VerbProperties.AdjustedAccuracy per range band.
	AccuracyTouch, AccuracyShort, AccuracyMedium, AccuracyLong float64
	// ProjectileSpeed is the projectile's SpeedTilesPerTick.
	ProjectileSpeed float64
	// DamageAmount is Projectile.DamageAmount and ArmorPenetration the
	// projectile's armor penetration, both after the weapon's quality.
	DamageAmount     int
	ArmorPenetration float64
	// ForcedMissRadius is VerbProperties.ForcedMissRadius; above 0.5 (mortars
	// and the like) the weapon is not ordinary ranged fire and is unsupported.
	ForcedMissRadius float64
}

// ShooterStats are the pawn stats ShotReport and the verb read from the
// shooter. Unknown marks the scenario unsupported.
type ShooterStats struct {
	ShootingAccuracyPawn                                       domain.Fact[float64]
	AccuracyTouch, AccuracyShort, AccuracyMedium, AccuracyLong domain.Fact[float64] // ShootingAccuracyFactor_*
	AimingDelayFactor                                          domain.Fact[float64]
	RangedCooldownFactor                                       domain.Fact[float64]
}

// CombatSpec is what makes a pawn a combatant.
type CombatSpec struct {
	// BodySize is Pawn.BodySize.
	BodySize domain.Fact[float64]
	// CanBenefitFromCover is the race's CanBenefitFromCover flag.
	CanBenefitFromCover bool
	// DamageToDown and DamageToKill are the post-armor damage totals at which
	// the injury approximation downs and kills the pawn (see approximations.md);
	// the caller derives them from the pawn's health, DamageToKill >=
	// DamageToDown > 0.
	DamageToDown, DamageToKill domain.Fact[float64]
	// Armor lists the armor ratings against the projectile's damage category,
	// in the order the game applies them: worn apparel from the last-worn
	// layer in, then the pawn's own natural rating. Known(nil) is unarmored.
	Armor domain.Fact[[]float64]
	// Weapon is nil for an unarmed pawn, which cannot be ordered to attack.
	Weapon  *WeaponDef
	Shooter ShooterStats
}

type stance uint8

const (
	stanceIdle     stance = iota
	stanceWarmup          // Stance_Warmup: aiming
	stanceBurst           // verb Bursting (with the between-shots Stance_Cooldown)
	stanceCooldown        // Stance_Cooldown after the burst
)

// combatant is a pawn's resolved combat state.
type combatant struct {
	has, armed bool
	bodySize   float64
	cover      bool
	downAt     float64
	killAt     float64
	armor      []float64
	weapon     WeaponDef
	sh         struct{ acc, touch, short, medium, long, aim, cooldown float64 }

	damage       float64
	downed, dead bool

	stance     stance
	stanceLeft int // ticks until warmup completes or cooldown ends
	cast       int // pawn index the current warmup or burst shoots at
	target     int // pawn index of the attack order, -1 none
	burstLeft  int
	nextShot   int // ticks until the next burst shot
}

func (c *combatant) cancelAttack() {
	c.target = -1
	if c.stance == stanceWarmup {
		c.stance = stanceIdle
	}
}

func need(f domain.Fact[float64], what, id string) (float64, error) {
	v, ok := f.Value()
	if !ok {
		return 0, unsupported("pawn %q %s unknown", id, what)
	}
	return v, nil
}

func (p *pawn) loadCombat(ps PawnSpec) error {
	cs := ps.Combat
	id := string(ps.ID)
	if ps.Side != combatmetrics.Colonist && ps.Side != combatmetrics.Hostile {
		return unsupported("combat pawn %q has side %q", id, ps.Side)
	}
	var err error
	c := &p.combatant
	c.has = true
	c.cover = cs.CanBenefitFromCover
	c.target = -1
	if c.bodySize, err = need(cs.BodySize, "BodySize", id); err != nil {
		return err
	}
	if c.downAt, err = need(cs.DamageToDown, "DamageToDown", id); err != nil {
		return err
	}
	if c.killAt, err = need(cs.DamageToKill, "DamageToKill", id); err != nil {
		return err
	}
	if !(c.bodySize > 0) || !(c.downAt > 0) || c.killAt < c.downAt {
		return unsupported("pawn %q body size %v or damage thresholds %v/%v are invalid", id, c.bodySize, c.downAt, c.killAt)
	}
	armor, ok := cs.Armor.Value()
	if !ok {
		return unsupported("pawn %q armor unknown", id)
	}
	c.armor = append([]float64(nil), armor...)
	if cs.Weapon == nil {
		return nil
	}
	w := *cs.Weapon
	switch {
	case !(w.Range > 0) || w.MinRange < 0 || w.MinRange > w.Range,
		w.WarmupSeconds < 0 || w.CooldownSeconds < 0,
		w.BurstShotCount < 1 || (w.BurstShotCount > 1 && w.TicksBetweenBurstShots < 1),
		!(w.AccuracyTouch > 0) || !(w.AccuracyShort > 0) || !(w.AccuracyMedium > 0) || !(w.AccuracyLong > 0),
		!(w.ProjectileSpeed > 0) || w.DamageAmount < 1 || w.ArmorPenetration < 0:
		return unsupported("pawn %q weapon %q has a missing or invalid value: %+v", id, w.Name, w)
	case w.ForcedMissRadius > 0.5:
		return unsupported("pawn %q weapon %q has a forced miss radius, which is not ordinary ranged fire", id, w.Name)
	}
	c.armed, c.weapon = true, w
	st := cs.Shooter
	for _, f := range []struct {
		dst  *float64
		fact domain.Fact[float64]
		what string
	}{
		{&c.sh.acc, st.ShootingAccuracyPawn, "ShootingAccuracyPawn"},
		{&c.sh.touch, st.AccuracyTouch, "ShootingAccuracyFactor_Touch"},
		{&c.sh.short, st.AccuracyShort, "ShootingAccuracyFactor_Short"},
		{&c.sh.medium, st.AccuracyMedium, "ShootingAccuracyFactor_Medium"},
		{&c.sh.long, st.AccuracyLong, "ShootingAccuracyFactor_Long"},
		{&c.sh.aim, st.AimingDelayFactor, "AimingDelayFactor"},
		{&c.sh.cooldown, st.RangedCooldownFactor, "RangedCooldownFactor"},
	} {
		if *f.dst, err = need(f.fact, f.what, id); err != nil {
			return err
		}
	}
	return nil
}

// loadCombatWorld reads the scenario-wide combat facts once any combatant exists.
func (s *Sim) loadCombatWorld(sc Scenario) error {
	any := false
	for i := range s.pawns {
		any = any || s.pawns[i].has
	}
	if !any {
		return nil
	}
	var ok bool
	if s.wxAcc, ok = sc.WeatherAccuracy.Value(); !ok || !(s.wxAcc > 0) {
		return unsupported("weather accuracy multiplier unknown or not positive")
	}
	if s.ff, ok = sc.FriendlyFireChanceFactor.Value(); !ok || s.ff < 0 {
		return unsupported("friendly fire chance factor unknown or negative")
	}
	return nil
}

func secondsToTicks(sec float64) int { return int(math.RoundToEven(sec * 60)) }

func (s *Sim) startAttack(i int, o Order) error {
	p := &s.pawns[i]
	ti := s.pawnIndex(o.Target)
	switch {
	case ti < 0:
		return unknownPawn(o.Target)
	case !p.armed:
		return fmt.Errorf("combatsim: pawn %q has no ranged weapon to attack with", p.id)
	case !s.pawns[ti].has || s.pawns[ti].side == p.side:
		return fmt.Errorf("combatsim: pawn %q cannot attack %q: not a combatant of the other side", p.id, o.Target)
	}
	if p.target != ti {
		p.cancelAttack()
	}
	p.target = ti
	return nil
}

// canHitFrom ports Verb.CanHitTargetFrom for a pawn at its own cell: the
// target within [min range, range] and in line of fire.
func (s *Sim) canHitFrom(si, ti int) bool {
	a, b := &s.pawns[si], &s.pawns[ti]
	dx, dz := float64(b.pos.X-a.pos.X), float64(b.pos.Z-a.pos.Z)
	d2 := dx*dx + dz*dz
	if d2 > a.weapon.Range*a.weapon.Range || d2 < a.weapon.MinRange*a.weapon.MinRange {
		return false
	}
	return s.CellCanSeeCell(a.pos, b.pos)
}

// combatTick runs one pawn's stance, job and verb phases in the order
// Pawn.Tick does (the pather already ran): stances, then jobs, then the
// equipment's verbs.
func (s *Sim) combatTick(i int) {
	p := &s.pawns[i]
	if !p.armed || p.dead || p.downed {
		return
	}
	// Stance_Warmup.StanceTick and Stance_Cooldown/Stance_Busy.StanceTick.
	switch p.stance {
	case stanceWarmup:
		t := &s.pawns[p.cast]
		if t.downed || t.dead || !s.canHitFrom(i, p.cast) {
			p.stance = stanceIdle
			break
		}
		if p.stanceLeft--; p.stanceLeft <= 0 {
			s.warmupComplete(i)
		}
	case stanceCooldown:
		if p.stanceLeft--; p.stanceLeft <= 0 {
			p.stance = stanceIdle
		}
	}
	// JobDriver_AttackStatic: start the verb whenever the pawn is not busy.
	if p.stance == stanceIdle && p.target >= 0 {
		t := &s.pawns[p.target]
		if t.downed || t.dead {
			s.emit(Event{Kind: EventAttackEnded, Pawn: p.id, Target: t.id})
			p.target = -1
		} else if s.canHitFrom(i, p.target) {
			p.cast = p.target
			if p.weapon.WarmupSeconds > 0 {
				p.stance, p.stanceLeft = stanceWarmup, secondsToTicks(p.weapon.WarmupSeconds*p.sh.aim)
			} else {
				s.warmupComplete(i)
			}
		}
	}
	// Verb.VerbTick while Bursting.
	if p.stance == stanceBurst {
		if p.nextShot--; p.nextShot <= 0 {
			s.castBurstShot(i)
		}
	}
}

// warmupComplete ports Verb.WarmupComplete: start the burst and fire its
// first shot at once.
func (s *Sim) warmupComplete(i int) {
	p := &s.pawns[i]
	p.stance = stanceBurst
	p.burstLeft = p.weapon.BurstShotCount
	s.castBurstShot(i)
}

// castBurstShot ports Verb.TryCastNextBurstShot.
func (s *Sim) castBurstShot(i int) {
	p := &s.pawns[i]
	t := &s.pawns[p.cast]
	if !t.dead && s.CellCanSeeCell(p.pos, t.pos) { // Verb_LaunchProjectile.TryCastShot: stopBurstWithoutLos
		s.launch(i, p.cast)
		p.burstLeft--
	} else {
		p.burstLeft = 0
	}
	if p.burstLeft > 0 {
		p.nextShot = p.weapon.TicksBetweenBurstShots
		return
	}
	p.stance = stanceCooldown
	p.stanceLeft = secondsToTicks(p.weapon.CooldownSeconds * p.sh.cooldown)
}

// Resolved reports whether one side has no standing combatant left and which
// side that leaves standing. Both sides must have had combatants.
func (s *Sim) Resolved() (winner string, ok bool) {
	var colonists, hostiles int
	for i := range s.pawns {
		p := &s.pawns[i]
		if !p.has || p.downed || p.dead {
			continue
		}
		if p.side == combatmetrics.Colonist {
			colonists++
		} else {
			hostiles++
		}
	}
	switch {
	case hostiles == 0:
		return combatmetrics.Colonist, true
	case colonists == 0:
		return combatmetrics.Hostile, true
	}
	return "", false
}

// Damage returns the damage ledger so far: every landed projectile, in order,
// as combatmetrics rows. The slice aliases a buffer the next Step may grow.
func (s *Sim) Damage() []combatmetrics.Hit { return s.ledger }

// Metrics scores the fight so far with the shared combatmetrics definition:
// the staging read is every combatant standing at tick 0 and the final read is
// the current state with the whole ledger.
func (s *Sim) Metrics(fixture string) combatmetrics.Metrics {
	staged := map[string]string{}
	start := combatmetrics.Read{Tick: 0}
	end := combatmetrics.Read{Tick: s.tick, Damage: s.ledger}
	for i := range s.pawns {
		p := &s.pawns[i]
		if !p.has {
			continue
		}
		staged[string(p.id)] = p.side
		start.Pawns = append(start.Pawns, combatmetrics.Pawn{ID: string(p.id)})
		end.Pawns = append(end.Pawns, combatmetrics.Pawn{ID: string(p.id), Dead: p.dead, Downed: p.downed && !p.dead})
	}
	return combatmetrics.Aggregate(fixture, staged, []combatmetrics.Read{start, end})
}
