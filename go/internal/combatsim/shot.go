package combatsim

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ShotReport is the game's ShotReport for one shooter and target, with every
// factor kept apart so tests (and later calibration) can see which moved.
type ShotReport struct {
	Distance float64
	// FromShooter is ShotReport.HitFactorFromShooter, FromEquipment
	// VerbProperties.GetHitChanceFactor, FromWeather the weather accuracy (1
	// when both cells are roofed), FromTargetSize the clamped body size,
	// FromPosture and FromExecution the lying-target factors.
	FromShooter, FromEquipment, FromWeather, FromTargetSize, FromPosture, FromExecution float64
	// CoverBlock is CoverUtility.CalculateOverallBlockChance.
	CoverBlock float64
	// AimStandard is AimOnTargetChance_StandardTarget, AimIgnoringPosture
	// AimOnTargetChance_IgnoringPosture (the chance a shot is not wild), Aim
	// AimOnTargetChance, and Total TotalEstimatedHitChance.
	AimStandard, AimIgnoringPosture, Aim, Total float64
}

// PassCover is PassCoverChance: the chance a shot gets past cover.
func (r ShotReport) PassCover() float64 { return 1 - r.CoverBlock }

// minAim is the floor ShotReport puts under the aim chance.
const minAim = 0.0201

func lerp(a, b, t float64) float64 { return a + (b-a)*math.Min(math.Max(t, 0), 1) }

// bands ports the four-band distance interpolation shared by
// HitFactorFromShooter and VerbProperties.GetHitChanceFactor.
func bands(d, touch, short, medium, long float64) float64 {
	switch {
	case d <= 3:
		return touch
	case d <= 12:
		return lerp(touch, short, (d-3)/9)
	case d <= 25:
		return lerp(short, medium, (d-12)/13)
	case d <= 40:
		return lerp(medium, long, (d-25)/15)
	}
	return long
}

// lying reports a pawn not standing: the game's posture for a downed pawn.
func (p *pawn) lying() bool { return p.downed || p.dead }

// report ports ShotReport.HitReportFor for the pawn at index si shooting the
// pawn at ti, from the shooter's own cell. Darkness offsets (Ideology) and
// gas are zero and one.
func (s *Sim) report(si, ti int) ShotReport {
	a, b := &s.pawns[si], &s.pawns[ti]
	dx, dz := float64(b.pos.X-a.pos.X), float64(b.pos.Z-a.pos.Z)
	r := ShotReport{Distance: math.Sqrt(dx*dx + dz*dz), FromPosture: 1, FromExecution: 1}
	d := r.Distance

	// HitFactorFromShooter: accuracy^distance scaled by the range-band stat.
	r.FromShooter = math.Max(math.Pow(a.sh.acc, d)*bands(d, a.sh.touch, a.sh.short, a.sh.medium, a.sh.long), minAim)
	w := &a.weapon
	r.FromEquipment = math.Min(math.Max(bands(d, w.AccuracyTouch, w.AccuracyShort, w.AccuracyMedium, w.AccuracyLong), 0.01), 1)
	r.CoverBlock = s.CoverBlockChance(b.pos, a.pos, b.cover)
	r.FromWeather = 1
	if !s.g.roofed[s.g.idx(a.pos)] || !s.g.roofed[s.g.idx(b.pos)] {
		r.FromWeather = s.wxAcc
	}
	r.FromTargetSize = math.Min(math.Max(b.bodySize, 0.5), 2)
	if b.lying() {
		if d >= 4.5 {
			r.FromPosture = 0.5
		}
		if d <= 3.9 {
			r.FromExecution = 7.5
		}
	}
	r.AimStandard = math.Max(r.FromShooter*r.FromEquipment*r.FromWeather*r.FromExecution, minAim)
	r.AimIgnoringPosture = r.AimStandard * r.FromTargetSize
	r.Aim = r.AimIgnoringPosture * r.FromPosture
	r.Total = math.Min(math.Max(r.Aim*r.PassCover(), 0), 1)
	return r
}

// ShotReport is the hit report for shooter firing at target from where they
// stand now. It does not check range or line of sight.
func (s *Sim) ShotReport(shooter, target domain.PawnID) (ShotReport, error) {
	si, ti := s.pawnIndex(shooter), s.pawnIndex(target)
	switch {
	case si < 0:
		return ShotReport{}, unknownPawn(shooter)
	case ti < 0:
		return ShotReport{}, unknownPawn(target)
	case !s.pawns[si].armed || !s.pawns[ti].has:
		return ShotReport{}, fmt.Errorf("combatsim: %q cannot report on %q: needs an armed shooter and a combatant target", shooter, target)
	}
	return s.report(si, ti), nil
}

// projectile is a launched shot whose outcome is decided; it lands at tick at.
type projectile struct {
	at               int
	shooter, victim  int32
	intended, distal bool // victim is the aimed-at pawn; the shot was >= 4.5 cells
}

// chance ports Rand.Chance on the physical stream.
func (s *Sim) chance(c float64) bool {
	switch {
	case c >= 1:
		return true
	case c <= 0:
		return false
	}
	return s.streams.Physical.Float64() < c
}

// roundRandom ports GenMath.RoundRandom.
func (s *Sim) roundRandom(f float64) float64 {
	n := math.Trunc(f)
	if s.streams.Physical.Float64() < f-n {
		n++
	}
	return n
}

// launch ports Verb_LaunchProjectile.TryCastShot for a shot from pawn si at
// ti, drawing the outcome now: wild, into cover, or a flight whose
// interception is decided against the pawns standing now.
func (s *Sim) launch(si, ti int) {
	a, t := &s.pawns[si], &s.pawns[ti]
	r := s.report(si, ti)
	e := Event{Kind: EventShot, Pawn: a.id, Target: t.id, Cell: a.pos, Outcome: ShotFlying}
	switch {
	case !s.chance(r.AimIgnoringPosture):
		e.Outcome = ShotWild
	case t.cover && !s.chance(r.PassCover()):
		e.Outcome = ShotCover
	}
	s.emit(e)
	if e.Outcome != ShotFlying {
		return
	}
	victim, at := s.fly(si, ti)
	intended := false
	if victim < 0 { // reaches the destination cell
		victim, at = ti, s.tick+s.flightTicks(si, ti)
		intended = true
	}
	s.pending = append(s.pending, projectile{
		at: at, shooter: int32(si), victim: int32(victim),
		intended: intended, distal: r.Distance*r.Distance >= 20.25,
	})
}

func (s *Sim) flightTicks(si, ti int) int {
	return max(1, int(math.Ceil(s.startingTicks(si, ti))))
}

// startingTicks is Projectile.StartingTicksToImpact between two cell centres.
func (s *Sim) startingTicks(si, ti int) float64 {
	a, b := s.pawns[si].pos, s.pawns[ti].pos
	dx, dz := float64(b.X-a.X), float64(b.Z-a.Z)
	n := math.Sqrt(dx*dx+dz*dz) / s.pawns[si].weapon.ProjectileSpeed
	if n <= 0 {
		return 0.001
	}
	return n
}

// interceptFactor ports VerbUtility.InterceptChanceFactorFromDistance: no
// chance within 5 cells of the origin, full from 12.
func interceptFactor(ox, oz float64, c domain.Cell) float64 {
	dx, dz := float64(c.X)+0.5-ox, float64(c.Z)+0.5-oz
	switch d2 := dx*dx + dz*dz; {
	case d2 <= 25:
		return 0
	case d2 >= 144:
		return 1
	default:
		return (d2 - 25) / (144 - 25)
	}
}

// fly ports Projectile.CheckForFreeInterceptBetween over a shot's whole
// flight, one segment per tick. A shot aimed at a pawn carries only the
// IntendedTarget and NonTargetPawns hit flags, so only other pawns can
// intercept it (walls and cover were settled by line of fire and the cover
// roll). It returns the intercepting pawn and the tick it lands, or -1.
func (s *Sim) fly(si, ti int) (victim, at int) {
	a, t := &s.pawns[si], &s.pawns[ti]
	ox, oz := float64(a.pos.X)+0.5, float64(a.pos.Z)+0.5
	tx, tz := float64(t.pos.X)+0.5, float64(t.pos.Z)+0.5
	start := s.startingTicks(si, ti)
	total := max(1, int(math.Ceil(start)))
	last := [2]float64{ox, oz}
	for k := 1; k <= total; k++ {
		frac := math.Min(math.Max(1-float64(total-k)/start, 0), 1)
		now := [2]float64{ox + (tx-ox)*frac, oz + (tz-oz)*frac}
		if v := s.interceptBetween(si, ti, ox, oz, last, now); v >= 0 {
			return v, s.tick + k
		}
		last = now
	}
	return -1, 0
}

func (s *Sim) interceptBetween(si, ti int, ox, oz float64, last, now [2]float64) int {
	if last == now {
		return -1
	}
	from := domain.Cell{X: int32(last[0]), Z: int32(last[1])}
	to := domain.Cell{X: int32(now[0]), Z: int32(now[1])}
	if to == from || !s.g.inBounds(from) || !s.g.inBounds(to) {
		return -1
	}
	dest := s.pawns[ti].pos
	if cardinalAdjacent(to, from) {
		return s.interceptAt(si, to, dest, ox, oz)
	}
	if interceptFactor(ox, oz, to) <= 0 {
		return -1
	}
	vx, vz := now[0]-last[0], now[1]-last[1]
	mag := math.Hypot(vx, vz)
	stepX, stepZ := vx/mag*0.2, vz/mag*0.2
	n := int(mag / 0.2)
	var seen [64]domain.Cell
	nseen := 0
	px, pz := last[0], last[1]
	for steps := 0; ; {
		px, pz = px+stepX, pz+stepZ
		c := domain.Cell{X: int32(px), Z: int32(pz)}
		fresh := true
		for _, sc := range seen[:nseen] {
			fresh = fresh && sc != c
		}
		if fresh {
			if v := s.interceptAt(si, c, dest, ox, oz); v >= 0 {
				return v
			}
			if nseen < len(seen) {
				seen[nseen] = c
				nseen++
			}
		}
		if steps++; steps > n || c == to {
			return -1
		}
	}
}

// interceptAt ports Projectile.CheckForFreeIntercept for the pawn on c.
func (s *Sim) interceptAt(si int, c, dest domain.Cell, ox, oz float64) int {
	if c == dest || !s.g.inBounds(c) {
		return -1
	}
	f := interceptFactor(ox, oz, c)
	if f <= 0 {
		return -1
	}
	j := int(s.occ[s.g.idx(c)]) - 1
	if j < 0 || j == si || !s.pawns[j].has {
		return -1
	}
	v := &s.pawns[j]
	chance := 0.4 * math.Min(math.Max(v.bodySize, 0.1), 2)
	if v.lying() {
		chance *= 0.1
	}
	if v.side == s.pawns[si].side {
		chance *= s.ff
	}
	chance *= f
	if chance > 1e-5 && s.chance(chance) {
		return j
	}
	return -1
}

// landProjectiles resolves every projectile due this tick, in launch order.
func (s *Sim) landProjectiles() {
	keep := s.pending[:0]
	for _, pr := range s.pending {
		if pr.at > s.tick {
			keep = append(keep, pr)
			continue
		}
		v := &s.pawns[pr.victim]
		if v.dead || !v.has {
			continue
		}
		// Projectile.ImpactSomething: a lying target is missed half the time
		// from 4.5 cells out. An interception skips that test.
		if pr.intended && v.lying() && pr.distal && !s.chance(0.5) {
			continue
		}
		s.damage(int(pr.shooter), int(pr.victim))
	}
	s.pending = keep
}

// damage lands one projectile of shooter si on victim vi: armor, then the
// injury approximation, then the ledger.
func (s *Sim) damage(si, vi int) {
	a, v := &s.pawns[si], &s.pawns[vi]
	dealt := s.afterArmor(float64(a.weapon.DamageAmount), a.weapon.ArmorPenetration, v.armor)
	if dealt > 0 && len(v.layers) > 0 {
		dealt *= float64(1 + s.hitCopies(v.layers))
	}
	v.damage += dealt
	downed, dead := v.downed, v.dead
	switch {
	case v.damage >= v.killAt:
		downed, dead = true, true
	case v.damage >= v.downAt:
		downed = true
		// Pawn_HealthTracker.CheckForStateChange: the hit that makes a
		// non-player pawn downable rolls the death-on-downed chance (A33).
		if !v.downed && s.chance(v.deathOnDown) {
			dead = true
		}
	}
	s.emit(Event{Kind: EventHit, Pawn: a.id, Target: v.id, Amount: dealt, Cell: v.pos})
	s.ledger = append(s.ledger, combatmetrics.Hit{
		Tick: s.tick, Victim: string(v.id), VictimSide: v.side,
		Instigator: string(a.id), InstigatorSide: a.side,
		Dealt: dealt, Downed: downed, Dead: dead,
	})
	if downed && !v.downed {
		v.downed = true
		v.stance, v.target, v.burstLeft = stanceIdle, -1, 0
		v.halt()
		s.emit(Event{Kind: EventDowned, Pawn: v.id, Target: a.id, Cell: v.pos})
	}
	if dead && !v.dead {
		v.dead = true
		s.occ[s.g.idx(v.pos)] = 0
		s.emit(Event{Kind: EventDied, Pawn: v.id, Target: a.id, Cell: v.pos})
	}
}

// hitCopies draws where a hit lands (A34): the number of further injuries of
// the same amount it adds.
func (s *Sim) hitCopies(layers []HitLayer) int {
	u := s.streams.Physical.Float64()
	for _, l := range layers {
		if u -= l.Chance; u < 0 {
			return l.Copies
		}
	}
	return layers[len(layers)-1].Copies
}

// afterArmor ports ArmorUtility.GetPostArmorDamage for a pawn without body
// part coverage: each rating, outermost first, either stops the damage,
// halves it or lets it through.
func (s *Sim) afterArmor(amount, penetration float64, ratings []float64) float64 {
	for _, rating := range ratings {
		eff := math.Max(rating-penetration, 0)
		switch roll := s.streams.Physical.Float64(); {
		case roll < eff*0.5:
			amount = 0
		case roll < eff:
			amount = s.roundRandom(amount / 2)
		}
		if amount < 0.001 {
			return 0
		}
	}
	return amount
}
