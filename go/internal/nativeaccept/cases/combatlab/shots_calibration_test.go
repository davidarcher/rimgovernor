package combatlab

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// shotStats are the shot-timing quantities of #2933, read the same way from
// the real shot dataset and from the sim's event trace, so the two can be set
// side by side. Sides are the staged fighters only (colonists, hostiles).
type shotStats struct {
	// pawns and shots count the fighters that fired and their shots, by side.
	pawns, shots map[string]int
	// opening is, per side, the first-shot tick of the fighters in the order
	// they opened (rank 0 is the first to fire), as lists across seeds.
	opening map[string][][]int
	// flight is launch to impact in ticks over every shot that impacted.
	flight []int
	// contact is the first projectile hit on a pawn per seed.
	contact []int
	// gaps are the ticks between a fighter's consecutive shots.
	gaps []int
	// cover and pawnHit count shots whose projectile stopped in a non-pawn
	// thing (real) or was absorbed by cover (sim), and shots that hit a pawn.
	cover, pawnHit map[string]int
	// early counts the hits on colonists within the first 700 ticks, seeds the
	// runs read, and downs the tick each raider was first downed (sim only: the
	// dataset's impacts carry no verdict).
	early, seeds int
	downs        []int
	// ends is each seed's last shot tick, the fight's length as the shooters
	// saw it.
	ends []int
	// hist and lasts are, per side, each firing pawn's shot count and last
	// shot tick.
	hist, lasts map[string][]int
}

func newShotStats() *shotStats {
	return &shotStats{
		pawns: map[string]int{}, shots: map[string]int{}, opening: map[string][][]int{},
		cover: map[string]int{}, pawnHit: map[string]int{},
		hist: map[string][]int{}, lasts: map[string][]int{},
	}
}

// seedShots is one seed's launches in order.
type seedShot struct {
	tick     int
	shooter  string
	side     string
	flight   int // launch to landing, -1 when it never landed
	covered  bool
	hitsPawn bool
}

// fold adds one seed's launches and its first pawn hit (-1 for none).
func (st *shotStats) fold(shots []seedShot, contact int) {
	first := map[string]map[string]int{}
	last := map[string]int{}
	for _, s := range shots {
		st.shots[s.side]++
		if first[s.side] == nil {
			first[s.side] = map[string]int{}
		}
		if _, ok := first[s.side][s.shooter]; !ok {
			first[s.side][s.shooter] = s.tick
			st.pawns[s.side]++
		} else {
			st.gaps = append(st.gaps, s.tick-last[s.shooter])
		}
		last[s.shooter] = s.tick
		if s.flight >= 0 {
			st.flight = append(st.flight, s.flight)
		}
		if s.covered {
			st.cover[s.side]++
		}
		if s.hitsPawn {
			st.pawnHit[s.side]++
		}
	}
	count := map[string]int{}
	lastShot := map[string]int{}
	for _, s := range shots {
		count[s.shooter]++
		lastShot[s.shooter] = s.tick
	}
	for _, s := range shots {
		if _, ok := count[s.shooter]; ok {
			st.hist[s.side] = append(st.hist[s.side], count[s.shooter])
			st.lasts[s.side] = append(st.lasts[s.side], lastShot[s.shooter])
			delete(count, s.shooter)
		}
	}
	for side, m := range first {
		var ticks []int
		for _, t := range m {
			ticks = append(ticks, t)
		}
		sort.Ints(ticks)
		for len(st.opening[side]) < len(ticks) {
			st.opening[side] = append(st.opening[side], nil)
		}
		for r, t := range ticks {
			st.opening[side][r] = append(st.opening[side][r], t)
		}
	}
	if contact >= 0 {
		st.contact = append(st.contact, contact)
	}
	if len(shots) > 0 {
		st.ends = append(st.ends, shots[len(shots)-1].tick)
	}
}

func mean(xs []int) float64 {
	sum := 0
	for _, x := range xs {
		sum += x
	}
	return float64(sum) / float64(max(len(xs), 1))
}

func quantile(xs []int, q float64) float64 {
	if len(xs) == 0 {
		return -1
	}
	s := slices.Clone(xs)
	sort.Ints(s)
	pos := q * float64(len(s)-1)
	lo := int(pos)
	if lo+1 >= len(s) {
		return float64(s[lo])
	}
	return float64(s[lo]) + (pos-float64(lo))*float64(s[lo+1]-s[lo])
}

// burstGap splits the gaps at the sim's longest between-shot spacing of a
// burst: the real rifle fires its burst 11 ticks apart (Stance_Cooldown left
// 11) and idles at least a cooldown and a warmup between bursts.
const burstGapLimit = 30

func (st *shotStats) inBurst() (share, medianIn, medianOut float64) {
	var in, out []int
	for _, g := range st.gaps {
		if g <= burstGapLimit {
			in = append(in, g)
		} else {
			out = append(out, g)
		}
	}
	return float64(len(in)) / float64(max(len(st.gaps), 1)), quantile(in, 0.5), quantile(out, 0.5)
}

func (st *shotStats) perPawn(side string) float64 {
	return float64(st.shots[side]) / float64(max(st.pawns[side], 1))
}

func (st *shotStats) share(m map[string]int, side string) float64 {
	return float64(m[side]) / float64(max(st.shots[side], 1))
}

func (st *shotStats) openings(side string) string {
	var parts []string
	for _, ts := range st.opening[side] {
		parts = append(parts, fmt.Sprintf("%.0f", quantile(ts, 0.5)))
	}
	return strings.Join(parts, "/")
}

func (st *shotStats) String() string {
	var b strings.Builder
	in, mi, mo := st.inBurst()
	fmt.Fprintf(&b, "openings colonist %s raider %s\n", st.openings(combatmetrics.Colonist), st.openings(combatmetrics.Hostile))
	fmt.Fprintf(&b, "shots/pawn colonist %.2f raider %.2f (%d shots by %d, %d shots by %d)\n", st.perPawn(combatmetrics.Colonist), st.perPawn(combatmetrics.Hostile),
		st.shots[combatmetrics.Colonist], st.pawns[combatmetrics.Colonist], st.shots[combatmetrics.Hostile], st.pawns[combatmetrics.Hostile])
	fmt.Fprintf(&b, "flight p25/p50/p75 %.0f/%.0f/%.0f mean %.2f (n=%d)\n", quantile(st.flight, .25), quantile(st.flight, .5), quantile(st.flight, .75), mean(st.flight), len(st.flight))
	fmt.Fprintf(&b, "first contact p25/p50/p75 %.0f/%.0f/%.0f (n=%d)\n", quantile(st.contact, .25), quantile(st.contact, .5), quantile(st.contact, .75), len(st.contact))
	fmt.Fprintf(&b, "burst: %.3f of gaps within a burst, median in %.0f out %.0f\n", in, mi, mo)
	var out []int
	for _, g := range st.gaps {
		if g > burstGapLimit {
			out = append(out, g)
		}
	}
	fmt.Fprintf(&b, "between-burst gaps p10/p25/p50/p75/p90/p99 %.0f/%.0f/%.0f/%.0f/%.0f/%.0f (n=%d)\n",
		quantile(out, .1), quantile(out, .25), quantile(out, .5), quantile(out, .75), quantile(out, .9), quantile(out, .99), len(out))
	fmt.Fprintf(&b, "cover share colonist %.3f raider %.3f; pawn-hit share colonist %.3f raider %.3f\n",
		st.share(st.cover, combatmetrics.Colonist), st.share(st.cover, combatmetrics.Hostile),
		st.share(st.pawnHit, combatmetrics.Colonist), st.share(st.pawnHit, combatmetrics.Hostile))
	for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
		h := map[int]int{}
		for _, n := range st.hist[side] {
			h[n]++
		}
		var keys []int
		for k := range h {
			keys = append(keys, k)
		}
		sort.Ints(keys)
		var parts []string
		for _, k := range keys {
			parts = append(parts, fmt.Sprintf("%d:%d", k, h[k]))
		}
		fmt.Fprintf(&b, "%s shots-per-pawn histogram %s; last shot p25/p50/p75 %.0f/%.0f/%.0f\n", side, strings.Join(parts, " "),
			quantile(st.lasts[side], .25), quantile(st.lasts[side], .5), quantile(st.lasts[side], .75))
	}
	fmt.Fprintf(&b, "last shot of the seed p25/p50/p75 %.0f/%.0f/%.0f\n", quantile(st.ends, .25), quantile(st.ends, .5), quantile(st.ends, .75))
	fmt.Fprintf(&b, "colonist hits on raiders in the first 700 ticks %.2f per seed; median raider down tick %.1f (n=%d)\n",
		float64(st.early)/float64(max(st.seeds, 1)), quantile(st.downs, .5), len(st.downs))
	return b.String()
}

// realShotStats reads the recorded dataset. Only the staged fighters count: a
// shot by an animal or a building is left out.
func realShotStats(set ShotSet) *shotStats {
	pawns := map[string]bool{}
	colonists := map[string]bool{}
	for _, s := range set.Shots {
		if s.Side == combatmetrics.Colonist || s.Side == combatmetrics.Hostile {
			pawns[s.Shooter] = true
		}
		colonists[s.Shooter] = colonists[s.Shooter] || s.Side == combatmetrics.Colonist
	}
	// The #2909 "first-700 hits" are the colonists' hits on raiders.
	shooters := map[[2]int]string{}
	for _, s := range set.Shots {
		shooters[[2]int{s.Seed, s.ID}] = s.Shooter
	}
	early := 0
	for _, im := range set.Impacts {
		if pawns[im.Hit] && !colonists[im.Hit] && colonists[shooters[[2]int{im.Seed, im.ID}]] && im.Tick <= 700 {
			early++
		}
	}
	type key struct{ seed, id int }
	impact := map[key]Impact{}
	contact := map[int]int{}
	for _, im := range set.Impacts {
		impact[key{im.Seed, im.ID}] = im
		if pawns[im.Hit] {
			if c, ok := contact[im.Seed]; !ok || im.Tick < c {
				contact[im.Seed] = im.Tick
			}
		}
	}
	bySeed := map[int][]seedShot{}
	var seeds []int
	for _, s := range set.Shots {
		if s.Side != combatmetrics.Colonist && s.Side != combatmetrics.Hostile {
			continue
		}
		if _, ok := bySeed[s.Seed]; !ok {
			seeds = append(seeds, s.Seed)
		}
		ss := seedShot{tick: s.Tick, shooter: s.Shooter, side: s.Side, flight: -1}
		if im, ok := impact[key{s.Seed, s.ID}]; ok {
			ss.flight = im.Tick - s.Tick
			ss.hitsPawn = pawns[im.Hit]
			ss.covered = im.Hit != "" && !ss.hitsPawn
		}
		bySeed[s.Seed] = append(bySeed[s.Seed], ss)
	}
	sort.Ints(seeds)
	st := newShotStats()
	for _, seed := range seeds {
		c, ok := contact[seed]
		if !ok {
			c = -1
		}
		st.fold(bySeed[seed], c)
	}
	st.early, st.seeds = early, len(seeds)
	return st
}

// simShotStats plays the fight for each seed and reads its event trace. It
// also returns the scored metrics of every run.
func simShotStats(fight combatsim.Fight, seeds []int) (*shotStats, []combatmetrics.Metrics, error) {
	side := map[string]string{}
	for _, p := range fight.Scenario.Pawns {
		side[string(p.ID)] = p.Side
	}
	st := newShotStats()
	var metrics []combatmetrics.Metrics
	for _, seed := range seeds {
		var shots []seedShot
		contact := -1
		// A flying shot's landing is its ETA; the shot that hit a pawn is
		// matched to its EventHit by the landing tick and the shooter.
		landing := map[[2]string][]int{}
		fight.Trace = func(events []combatsim.Event) {
			for _, e := range events {
				switch e.Kind {
				case combatsim.EventShot:
					ss := seedShot{tick: e.Tick, shooter: string(e.Pawn), side: side[string(e.Pawn)], flight: -1}
					ss.covered = e.StopsInThing
					if e.ETA > e.Tick {
						ss.flight = e.ETA - e.Tick
						k := [2]string{string(e.Pawn), fmt.Sprint(e.ETA)}
						landing[k] = append(landing[k], len(shots))
					}
					shots = append(shots, ss)
				case combatsim.EventDowned:
					if side[string(e.Pawn)] == combatmetrics.Hostile {
						st.downs = append(st.downs, e.Tick)
					}
				case combatsim.EventHit:
					if contact < 0 {
						contact = e.Tick
					}
					if side[string(e.Pawn)] == combatmetrics.Colonist && side[string(e.Target)] == combatmetrics.Hostile && e.Tick <= 700 {
						st.early++
					}
					k := [2]string{string(e.Pawn), fmt.Sprint(e.Tick)}
					if idx := landing[k]; len(idx) > 0 {
						shots[idx[0]].hitsPawn = true
						landing[k] = idx[1:]
					}
				}
			}
		}
		res, err := combatsim.RunFight(fight, uint64(seed), policy.DefaultCombatTuning())
		if err != nil {
			return nil, nil, fmt.Errorf("seed %d: %w", seed, err)
		}
		st.fold(shots, contact)
		st.seeds++
		metrics = append(metrics, res.Metrics)
	}
	return st, metrics, nil
}

func loadRecordedShots(t *testing.T) ShotSet {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("calibration", "shots", "lab-ranged.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	set, err := ParseShots(bytes.NewReader(data))
	if err != nil {
		t.Fatal(err)
	}
	return set
}

func shotLabFight(t *testing.T) combatsim.Fight {
	t.Helper()
	cx, cz := labCenter()
	fight, err := importFight(recordedcatalog.Catalog(t), "lab-ranged", cx, cz)
	if err != nil {
		t.Fatal(err)
	}
	return fight
}

func shotSeeds(set ShotSet) []int {
	seen := map[int]bool{}
	var seeds []int
	for _, s := range set.Shots {
		if !seen[s.Seed] {
			seen[s.Seed] = true
			seeds = append(seeds, s.Seed)
		}
	}
	sort.Ints(seeds)
	return seeds
}

// TestSimShotTimingMatchesTheRecordedShots plays the lab-ranged fight in the
// sim over the seeds of the recorded real shot dataset (#2917, 20 resumed
// runs) and compares the shot-timing quantities of #2933 within stated
// tolerances. The tolerances are bounds on what the sim is known to explain:
// each exact quantity is held to a tick or two, and the quantities that follow
// the health model (A1/A33/A34: real raiders go down on fewer hits) carry the
// wider bound the remaining gap needs, which approximations.md records.
func TestSimShotTimingMatchesTheRecordedShots(t *testing.T) {
	set := loadRecordedShots(t)
	real := realShotStats(set)
	sim, metrics, err := simShotStats(shotLabFight(t), shotSeeds(set))
	if err != nil {
		t.Fatal(err)
	}
	var dmg, downs, deaths float64
	var resolved []int
	for _, m := range metrics {
		dmg += m.ColonistDamage
		downs += float64(m.ColonistDowns)
		deaths += float64(m.EnemyDeaths)
		resolved = append(resolved, m.ResolvedTick)
	}
	n := float64(len(metrics))
	t.Logf("REAL\n%s", real)
	t.Logf("SIM\n%s", sim)
	t.Logf("sim colonist damage %.2f downs %.2f raider deaths %.2f resolved tick median %.1f", dmg/n, downs/n, deaths/n, quantile(resolved, .5))

	within := func(what string, got, want, tol float64) {
		t.Helper()
		if got < want-tol || got > want+tol {
			t.Errorf("%s: sim %.3f, real %.3f, tolerance %.3f", what, got, want, tol)
		}
	}
	// Openings: every rank of first shots, to 2 ticks (a warmup is 60 ticks;
	// movers arrive 29/29/57 and shoot 59 ticks later, raiders' warmup runs at
	// tick 0).
	for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
		rr, sr := real.opening[side], sim.opening[side]
		if len(rr) != len(sr) {
			t.Errorf("%s openings: sim %s, real %s", side, sim.openings(side), real.openings(side))
			continue
		}
		for r := range rr {
			within(fmt.Sprintf("%s opening rank %d median tick", side, r), quantile(sr[r], .5), quantile(rr[r], .5), 2)
		}
	}
	// Flight: launch to impact, quartiles to 2 ticks (the 3D flight plus the
	// projectile's interval phase; the sim keeps a residual tick short).
	for _, q := range []float64{.25, .5, .75} {
		within(fmt.Sprintf("flight p%.0f", q*100), quantile(sim.flight, q), quantile(real.flight, q), 2)
	}
	// First contact: the first projectile to strike a pawn, median to 5 ticks.
	within("first contact median", quantile(sim.contact, .5), quantile(real.contact, .5), 5)
	// Burst shape: the share of gaps inside a burst, the in-burst gap and the
	// between-burst cycle (exactly 162 in the game and the sim).
	inReal, medInReal, medOutReal := real.inBurst()
	inSim, medInSim, medOutSim := sim.inBurst()
	within("share of gaps within a burst", inSim, inReal, 0.03)
	within("median in-burst gap", medInSim, medInReal, 1)
	within("median between-burst gap", medOutSim, medOutReal, 1)
	// Shots per pawn: real raiders go down on fewer hits than the sim's, so
	// they fire less (A1/A33/A34: the median raider is down at tick 499.5 in
	// the real baseline, about 520 in the sim). The sim's excess stays inside
	// 20 percent.
	for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
		rp, sp := real.perPawn(side), sim.perPawn(side)
		within(side+" shots per pawn", sp, rp, 0.20*rp)
	}
	// Pawn hits per shot, both sides, within 5 points.
	for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
		within(side+" pawn-hit share", sim.share(sim.pawnHit, side), real.share(real.pawnHit, side), 0.05)
	}
	// Cover: no colonist shot strikes a sandbag in either; the raiders' shots
	// that stop in a sandbag (covered, or wild and landing on it, A7) are the
	// real 0.38 to 5 points (3 before the serious-injury stop, A39: the
	// retreating colonists leave their sandbag cover, so the sim's raiders
	// stop in a sandbag a little less often).
	within("colonist cover share", sim.share(sim.cover, combatmetrics.Colonist), real.share(real.cover, combatmetrics.Colonist), 0.01)
	within("raider cover share", sim.share(sim.cover, combatmetrics.Hostile), real.share(real.cover, combatmetrics.Hostile), 0.05)
	// The colonists' hits on raiders in the first 700 ticks, within 15 percent (the sim runs about 13 percent over: seed noise of the id-driven phases plus A1).
	rh, sh := float64(real.early)/float64(real.seeds), float64(sim.early)/float64(sim.seeds)
	within("colonist hits on raiders in the first 700 ticks", sh, rh, 0.15*rh)
}
