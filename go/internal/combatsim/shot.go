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
	at      int
	shooter int32
	// victim is the pawn the projectile strikes, or -1 when it stops in a
	// thing or on the ground.
	victim           int32
	intended, distal bool // victim is the aimed-at pawn; the shot was >= 4.5 cells
}

// hitFlags is ProjectileHitFlags: what a flying projectile may strike.
type hitFlags uint8

const (
	hitIntended hitFlags = 1 << iota
	hitNonTargetPawns
	hitNonTargetWorld
)

// strike is where a projectile ends: a pawn (index), a non-pawn thing, or
// neither (the ground, or off the map).
type strike struct {
	pawn  int
	thing bool
}

var noStrike = strike{pawn: -1}

// coverGiver is one CoverInfo of a ShotReport: the cell of a thing next to the
// target and the adjusted chance it blocks the shot.
type coverGiver struct {
	cell   domain.Cell
	chance float64
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
// ti, drawing the outcome now. A wild shot is sent to a cell around the target
// (ShootLine.ChangeDestToMissWild), a covered one at the cover thing next to
// it, and every projectile then flies to its destination, where the pawns and
// things on the way and at the end are rolled against the shot's hit flags
// (A7, A9). The interception is decided against the pawns standing now.
func (s *Sim) launch(si, ti int) {
	a, t := &s.pawns[si], &s.pawns[ti]
	r := s.report(si, ti)
	id := s.takeID() // every projectile takes an id, wild or not
	e := Event{Kind: EventShot, Pawn: a.id, Target: t.id, Cell: a.pos, Outcome: ShotFlying}
	// ShotReport.GetRandomCoverToMissInto is drawn before the wild test.
	coverCell, haveCover := s.randomCover(a.pos, t)
	dest, flags, used := t.pos, hitIntended|hitNonTargetPawns, usedPawn
	switch {
	case !s.chance(r.AimIgnoringPosture):
		e.Outcome = ShotWild
		dest = s.wildDest(a.pos, t.pos, r.AimStandard)
		flags, used = hitNonTargetWorld, usedCell
		if s.chance(0.5) {
			flags |= hitNonTargetPawns
		}
	case t.cover && haveCover && !s.chance(r.PassCover()):
		e.Outcome = ShotCover
		dest, flags, used = coverCell, hitNonTargetWorld|hitNonTargetPawns, usedCover
	}
	// Projectile.Launch: the destination is the cell centre plus a random
	// horizontal vector of up to 0.3 on each axis, and the projectile is ticked
	// by the TickList on its own thing id's 15-tick phase (A9).
	off := [2]float64{
		(s.streams.Physical.Float64()*2 - 1) * launchScatter,
		(s.streams.Physical.Float64()*2 - 1) * launchScatter,
	}
	fl := flight{si: si, ti: ti, dest: dest, flags: flags,
		tx: float64(dest.X) + 0.5 + off[0], tz: float64(dest.Z) + 0.5 + off[1]}
	end, at, arrived := s.fly(fl, idPhase(id))
	if arrived {
		end = s.impactSomething(fl, used)
	}
	e.ETA, e.StopsInThing = at, end.thing
	s.emit(e)
	if end.pawn < 0 { // it stops in a thing, on the ground or off the map
		return
	}
	intended := arrived && used == usedPawn
	s.pending = append(s.pending, projectile{
		at: at, shooter: int32(si), victim: int32(end.pawn),
		intended: intended, distal: r.Distance*r.Distance >= 20.25,
	})
}

// randomCover ports ShotReport.GetRandomCoverToMissInto: the cover thing next
// to t, weighted by its block chance.
func (s *Sim) randomCover(shooter domain.Cell, t *pawn) (domain.Cell, bool) {
	if !t.cover {
		return domain.Cell{}, false
	}
	s.givers = s.coverGivers(t.pos, shooter, s.givers[:0])
	total := 0.0
	for _, c := range s.givers {
		total += c.chance
	}
	if len(s.givers) == 0 || total <= 0 {
		return domain.Cell{}, false
	}
	u := s.streams.Physical.Float64() * total
	for _, c := range s.givers {
		if u -= c.chance; u < 0 {
			return c.cell, true
		}
	}
	return s.givers[len(s.givers)-1].cell, true
}

// missDistance evaluates ShootTuning.MissDistanceFromAimOnChanceCurves: the
// columns at aim-on chances 0.02, 0.04, 0.07, 0.11, 0.22 and 1 map a uniform
// draw u to a distance from 1 up to 10, 8, 6, 4, 2 and 1 cells, blended
// linearly between neighbouring columns.
func missDistance(aim, u float64) float64 {
	xs := [...]float64{0.02, 0.04, 0.07, 0.11, 0.22, 1}
	far := [...]float64{10, 8, 6, 4, 2, 1}
	col := func(i int) float64 { return 1 + (far[i]-1)*math.Min(math.Max(u, 0), 1) }
	if aim <= xs[0] {
		return col(0)
	}
	for i := 1; i < len(xs); i++ {
		if aim <= xs[i] {
			return lerp(col(i-1), col(i), (aim-xs[i-1])/(xs[i]-xs[i-1]))
		}
	}
	return col(len(xs) - 1)
}

// wildDest ports ShootLine.ChangeDestToMissWild for a shot from src at dest:
// a random point missDistance cells from the target's cell centre that does not
// lie behind the shooter, then, if the shooter cannot see that cell, the first
// filled cell on the line to it that is not within 1.5 cells of the shooter.
// The line starts at the shooter's own cell (A8).
func (s *Sim) wildDest(src, dest domain.Cell, aim float64) domain.Cell {
	num := missDistance(aim, s.streams.Physical.Float64())
	var c domain.Cell
	for {
		ux, uz := s.streams.Physical.NormFloat64(), s.streams.Physical.NormFloat64()
		if n := math.Hypot(ux, uz); n > 0 {
			ux, uz = ux/n, uz/n
		}
		// Vector3.ToIntVec3 truncates.
		c = domain.Cell{
			X: int32(float64(dest.X) + 0.5 + ux*num),
			Z: int32(float64(dest.Z) + 0.5 + uz*num),
		}
		dot := (dest.X-src.X)*(c.X-src.X) + (dest.Z-src.Z)*(c.Z-src.Z)
		if dot >= 0 {
			break
		}
	}
	if s.CellCanSeeCell(src, c) {
		return c
	}
	pointsOnLine(src, c, func(p domain.Cell) bool {
		c = p
		if p == src || !s.g.inBounds(p) || s.g.thing(p).Fillage != FillFull {
			return true
		}
		dx, dz := float64(src.X-p.X), float64(src.Z-p.Z)
		if dx*dx+dz*dz <= 1.5*1.5 {
			return true
		}
		return s.g.thing(p).Door && s.g.doorOpen[s.g.idx(p)]
	})
	return c
}

const (
	// launchScatter is the bound of Gen.RandomHorizontalVector(0.3f) that
	// Projectile.Launch adds to the destination cell centre.
	launchScatter = 0.3
	// offCameraInterval is the TickInterval cadence of a thing the camera does
	// not see: min(max(GenTicks.GetCameraUpdateRate, MinTickIntervalRate),
	// MaxTickIntervalRate) = 15. A lab run is off camera.
	offCameraInterval = 15
	// pawnAltitude is the y of a pawn's DrawPos, Altitudes.AltitudeFor(
	// AltitudeLayer.Pawn): layer 23 times the 0.36585367 layer spacing. The
	// projectile launches from DrawPos and lands on y 0, so the 3D distance
	// StartingTicksToImpact divides by speed is longer than the map distance.
	pawnAltitude = 23 * 0.36585367
)

// startingTicks is Projectile.StartingTicksToImpact for a shot from pawn si to
// the centre of pawn ti's cell shifted by off: the 3D origin-to-destination
// distance over the projectile speed.
func (s *Sim) startingTicks(si int, tx, tz float64) float64 {
	a := s.pawns[si].pos
	dx, dz := tx-(float64(a.X)+0.5), tz-(float64(a.Z)+0.5)
	n := math.Sqrt(dx*dx+dz*dz+pawnAltitude*pawnAltitude) / s.pawns[si].weapon.ProjectileSpeed
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

// fly ports Projectile.TickInterval and CheckForFreeInterceptBetween over a
// shot's whole flight. An off-camera projectile is ticked only on the ticks
// where (tick + its HashOffset) is a multiple of 15, each call advancing
// ticksToImpact by the ticks since the last one, and one interception segment
// is tested per call; it lands on the first call that brings ticksToImpact to
// 0 or below, so at the first tick of its phase at or after launch + ticks to
// impact (A9). phase is that tick's residue mod 15. A shot aimed at a pawn
// carries only the IntendedTarget and NonTargetPawns hit flags, so only other
// pawns can intercept it (walls and cover were settled by line of fire and the
// cover roll); a wild or covered shot carries NonTargetWorld, so it can also
// be stopped by a wall or a sandbag on the way. It returns where the
// projectile was stopped, the tick it lands or is destroyed, and whether it
// reached its destination (ImpactSomething follows). A projectile whose next
// position is off the map is destroyed there.
func (s *Sim) fly(fl flight, phase int) (end strike, at int, arrived bool) {
	a := &s.pawns[fl.si]
	ox, oz := float64(a.pos.X)+0.5, float64(a.pos.Z)+0.5
	start := s.startingTicks(fl.si, fl.tx, fl.tz)
	total := max(1, int(math.Ceil(start)))
	last := [2]float64{ox, oz}
	// The first call is the first tick of the phase at or after the launch.
	first := s.tick + ((phase-s.tick)%offCameraInterval+offCameraInterval)%offCameraInterval
	for at = first; ; at += offCameraInterval {
		left := total - (at - s.tick) // ticksToImpact after this call
		frac := math.Min(math.Max(1-float64(left)/start, 0), 1)
		now := [2]float64{ox + (fl.tx-ox)*frac, oz + (fl.tz-oz)*frac}
		if now[0] < 0 || now[1] < 0 || now[0] >= float64(s.g.w) || now[1] >= float64(s.g.h) {
			return noStrike, at, false
		}
		if v := s.interceptBetween(fl, ox, oz, last, now); v.thing || v.pawn >= 0 {
			return v, at, false
		}
		if left <= 0 {
			return noStrike, at, true
		}
		last = now
	}
}

// flight is a launched projectile's course: shooter si, intended target ti,
// the destination cell dest and its exact point (tx, tz), and the hit flags.
type flight struct {
	si, ti int
	dest   domain.Cell
	tx, tz float64
	flags  hitFlags
}

func (s *Sim) interceptBetween(fl flight, ox, oz float64, last, now [2]float64) strike {
	if last == now {
		return noStrike
	}
	from := domain.Cell{X: int32(last[0]), Z: int32(last[1])}
	to := domain.Cell{X: int32(now[0]), Z: int32(now[1])}
	if to == from || !s.g.inBounds(from) || !s.g.inBounds(to) {
		return noStrike
	}
	if cardinalAdjacent(to, from) {
		return s.interceptAt(fl, to, ox, oz)
	}
	if interceptFactor(ox, oz, to) <= 0 {
		return noStrike
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
			if v := s.interceptAt(fl, c, ox, oz); v.thing || v.pawn >= 0 {
				return v
			}
			if nseen < len(seen) {
				seen[nseen] = c
				nseen++
			}
		}
		if steps++; steps > n || c == to {
			return noStrike
		}
	}
}

// canHitPawn ports Projectile.CanHit for pawn j: not the launcher, the aimed
// target only with the IntendedTarget flag, any other pawn with
// NonTargetPawns.
func (fl flight) canHitPawn(j int) bool {
	switch j {
	case fl.si:
		return false
	case fl.ti:
		return fl.flags&hitIntended != 0
	}
	return fl.flags&hitNonTargetPawns != 0
}

// interceptAt ports Projectile.CheckForFreeIntercept for the things on c: the
// building or item there first, then the pawns in the order they entered it
// (the cell's thing list).
func (s *Sim) interceptAt(fl flight, c domain.Cell, ox, oz float64) strike {
	if c == fl.dest || !s.g.inBounds(c) {
		return noStrike
	}
	f := interceptFactor(ox, oz, c)
	if f <= 0 {
		return noStrike
	}
	if th := s.g.thing(c); fl.flags&hitNonTargetWorld != 0 && th.Name != "" {
		// A Full thing stops the projectile unless it is an open door, which
		// only gets the fixed 0.05; any other thing above fill 0.2 gets its
		// fill percent, a full share next to the destination, 0.15 of it
		// elsewhere.
		openDoor := th.Door && s.g.doorOpen[s.g.idx(c)]
		if th.Fillage == FillFull && !openDoor {
			return strike{pawn: -1, thing: true}
		}
		chance := 0.0
		switch {
		case th.FillPercent <= 0.2:
		case openDoor:
			chance = 0.05
		case max(abs32(c.X-fl.dest.X), abs32(c.Z-fl.dest.Z)) == 1:
			chance = th.FillPercent
		default:
			chance = th.FillPercent * 0.15
		}
		if chance *= f; chance > 1e-5 && s.chance(chance) {
			return strike{pawn: -1, thing: true}
		}
	}
	for k := s.cells.head[s.g.idx(c)]; k != 0; k = s.pawns[k-1].here {
		j := int(k) - 1
		if !s.pawns[j].has || !fl.canHitPawn(j) {
			continue
		}
		v := &s.pawns[j]
		chance := 0.4 * math.Min(math.Max(v.bodySize, 0.1), 2)
		if v.lying() {
			chance *= 0.1
		}
		if v.side == s.pawns[fl.si].side {
			chance *= s.ff
		}
		chance *= f
		if chance > 1e-5 && s.chance(chance) {
			return strike{pawn: j}
		}
	}
	return noStrike
}

// impactSomething ports Projectile.ImpactSomething for a projectile that
// reached its destination cell. A projectile aimed at a pawn (usedPawn)
// strikes it; one sent at a cover thing strikes that thing; one sent at a bare
// cell rolls, in random order, every pawn and thing standing there that it may
// hit: a pawn at 0.5 per body size (halved for a lying pawn from 4.5 cells
// out, and scaled by the distance factor for a friendly one), a thing at 1.5
// times its fill percent.
func (s *Sim) impactSomething(fl flight, used usedTarget) strike {
	switch used {
	case usedPawn:
		return strike{pawn: fl.ti}
	case usedCover:
		return strike{pawn: -1, thing: true}
	}
	var cands []strike
	if th := s.g.thing(fl.dest); fl.flags&hitNonTargetWorld != 0 && th.Name != "" {
		cands = append(cands, strike{pawn: -1, thing: true})
	}
	for k := s.cells.head[s.g.idx(fl.dest)]; k != 0; k = s.pawns[k-1].here {
		if j := int(k) - 1; fl.canHitPawn(j) {
			cands = append(cands, strike{pawn: j})
		}
	}
	s.streams.Physical.Shuffle(len(cands), func(i, j int) { cands[i], cands[j] = cands[j], cands[i] })
	ox, oz := float64(s.pawns[fl.si].pos.X)+0.5, float64(s.pawns[fl.si].pos.Z)+0.5
	far := (fl.tx-ox)*(fl.tx-ox)+(fl.tz-oz)*(fl.tz-oz) >= 20.25
	for _, c := range cands {
		chance := 0.0
		if c.thing {
			chance = 1.5 * s.g.thing(fl.dest).FillPercent
		} else {
			v := &s.pawns[c.pawn]
			chance = 0.5 * math.Min(math.Max(v.bodySize, 0.1), 2)
			if v.lying() && far {
				chance *= 0.5
			}
			if v.side == s.pawns[fl.si].side {
				chance *= interceptFactor(ox, oz, fl.dest)
			}
		}
		if s.chance(chance) {
			return c
		}
	}
	return noStrike
}

// usedTarget is what the projectile was launched at (Projectile.usedTarget).
type usedTarget uint8

const (
	// usedPawn: the aimed-at pawn.
	usedPawn usedTarget = iota
	// usedCover: the cover thing a covered shot is sent into.
	usedCover
	// usedCell: a cell, for a wild shot.
	usedCell
)

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
	if dealt > 0 {
		s.dropFilth()
	}
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
		s.leave(s.g.idx(v.pos), vi)
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
