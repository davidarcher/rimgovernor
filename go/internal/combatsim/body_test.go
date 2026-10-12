package combatsim

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Part indices of testAnatomy.
const (
	pTorso = iota
	pHeart
	pHead
	pBrain
	pArm
	pHand
)

// testAnatomy is a small body: a torso (core) holding a heart, a head holding a
// brain, and an arm with a hand. Pain is 0.01 per severity, shock at 0.8, and
// the lost-part pain is 0.02 per point of part health.
func testAnatomy() *Body {
	return &Body{
		Parts: []BodyPart{
			{Parent: -1, Coverage: 0.5, HitWeight: 0.5, MaxHealth: 40, Destroyable: true},
			{Parent: pTorso, Inside: true, Coverage: 0.1, HitWeight: 0.1, MaxHealth: 15, Solid: true, Destroyable: true},
			{Parent: pTorso, Coverage: 0.1, HitWeight: 0.1, MaxHealth: 25, Destroyable: true},
			{Parent: pHead, Inside: true, Coverage: 0.05, HitWeight: 0.05, MaxHealth: 10, Solid: true, Destroyable: true},
			{Parent: pTorso, Coverage: 0.15, HitWeight: 0.15, MaxHealth: 30, Destroyable: true},
			{Parent: pArm, Coverage: 0.1, HitWeight: 0.1, MaxHealth: 20, Destroyable: true},
		},
		HealthScale: 1, PainPerSeverity: 0.01, MissingPainPerSeverity: 0.02, PainShock: 0.8, LethalDamage: 150,
		IncomingDamageFactor: 1, OverkillLow: 0, OverkillHigh: 0.7, MovingMinForCapable: 0.15,
		Consciousness: []int{pBrain}, BloodPumping: []int{pHeart},
		Moving: []Limb{{Core: pTorso}}, Manipulation: []Limb{{Core: pArm, Segments: []int{pHand}}},
	}
}

func fresh(b *Body) injuryState { return newInjuryState(len(b.Parts)) }

func TestAddInjuryDestroysAPartWithItsSubtree(t *testing.T) {
	b := testAnatomy()
	st := fresh(b)
	b.addInjury(&st, pHand, 5)
	if dealt := b.addInjury(&st, pArm, 35); dealt != 30 {
		t.Errorf("dealt %v to a 30 health arm, want 30", dealt)
	}
	if !st.missing[pArm] || !st.missing[pHand] || !st.fresh[pArm] || st.sev[pHand] != 0 || st.total != 0 {
		t.Errorf("after the arm is destroyed: %+v", st)
	}
	// A lost outside, non-solid part hurts by its maximum health, once, through
	// the part itself and not the hand under it.
	if got, want := b.pain(&st), 30*0.02; math.Abs(got-want) > 1e-9 {
		t.Errorf("pain %v, want %v", got, want)
	}
	// The arm is gone, so nothing manipulates.
	if caps := b.levels(&st); caps.manipulation != 0 || caps.moving == 0 {
		t.Errorf("capacities %+v", caps)
	}
}

func TestCorePartIsNeverDestroyedButItsLossKills(t *testing.T) {
	b := testAnatomy()
	st := fresh(b)
	b.addInjury(&st, pTorso, 39)
	if b.shouldBeDead(&st, b.levels(&st)) {
		t.Error("a torso at 1 health lives")
	}
	b.addInjury(&st, pTorso, 1)
	if st.missing[pTorso] || !b.shouldBeDead(&st, b.levels(&st)) {
		t.Errorf("a torso at 0 health: missing %v, want dead and not missing", st.missing[pTorso])
	}
}

func TestVitalPartLossIsLethalAndTheBrainDownsAndKills(t *testing.T) {
	b := testAnatomy()
	st := fresh(b)
	b.addInjury(&st, pHeart, 15)
	if caps := b.levels(&st); !caps.lethalLost || !b.shouldBeDead(&st, caps) {
		t.Errorf("a destroyed heart: %+v", caps)
	}
	st = fresh(b)
	b.addInjury(&st, pBrain, 10)
	if caps := b.levels(&st); caps.consciousness != 0 || !caps.lethalLost {
		t.Errorf("a destroyed brain: %+v", caps)
	}
}

func TestPainShockDownsAtTheThreshold(t *testing.T) {
	b := testAnatomy()
	st := fresh(b)
	b.addInjury(&st, pTorso, 7) // 0.07 pain
	b.addInjury(&st, pHead, 10)
	if b.shouldBeDowned(&st, b.levels(&st)) {
		t.Error("17 severity downed")
	}
	b.addInjury(&st, pArm, 30)  // the arm is destroyed: 0.6 from the loss
	b.addInjury(&st, pTorso, 3) // a 20-point pain tips 0.07+0.1+0.6 over
	if pain := b.pain(&st); pain < 0.8 && !b.shouldBeDowned(&st, b.levels(&st)) {
		t.Errorf("pain %v not downed", pain)
	}
}

func TestWeaponDropsWhenTheHandsAreSpent(t *testing.T) {
	// b's only hittable part is its arm, 10 health. Each rifle hit is 10, so the
	// first leaves 1 health (an outside part is preserved: efficiency 0) and
	// the arm no longer holds a weapon; b never shoots again.
	sc := duel(t, rapid(), func(id domain.PawnID, c *CombatSpec) {
		if id == "a" {
			immortal(id, c)
			return
		}
		b := thresholdBody(1e9, 1e9)
		b.Parts[0].HitWeight = 0
		b.Parts = append(b.Parts, BodyPart{Parent: 0, Coverage: 1, HitWeight: 1, MaxHealth: 10, Destroyable: true})
		b.OverkillHigh = 0.7
		b.Manipulation = []Limb{{Core: 1}}
		c.Body = b
	}, "a.b")
	s := newSim(t, sc, 41)
	_, ev := runUntil(t, s, []Order{attack("a", "b"), attack("b", "a")}, 300, nil)
	firstHit := -1
	for _, e := range ofKind(ev, EventHit) {
		if e.Target == "b" {
			firstHit = e.Tick
			break
		}
	}
	if firstHit < 0 {
		t.Fatal("b was never hit")
	}
	for _, e := range ofKind(ev, EventShot) {
		if e.Pawn == "b" && e.Tick > firstHit {
			t.Fatalf("b shot at tick %d after dropping its weapon at %d", e.Tick, firstHit)
		}
	}
}

func TestOverkillChanceIsTheInverseLerpOfTheRange(t *testing.T) {
	b := testAnatomy()
	for _, c := range []struct{ f, want float64 }{{0, 0}, {0.35, 0.5}, {0.7, 1}, {2, 1}} {
		if got := b.overkillChance(c.f); math.Abs(got-c.want) > 1e-9 {
			t.Errorf("overkillChance(%v) = %v, want %v", c.f, got, c.want)
		}
	}
	b.OverkillHigh = 0
	if got := b.overkillChance(0.5); got != 0 {
		t.Errorf("an empty range gives %v, want 0", got)
	}
}

func TestChooseHitPartSkipsMissingPartsAndWeightlessOnes(t *testing.T) {
	b := testAnatomy()
	b.Parts[pTorso].HitWeight = 0
	st := fresh(b)
	st.missing[pArm] = true
	for _, u := range []float64{0, 0.3, 0.7, 0.999999} {
		if p := b.chooseHitPart(&st, u); p == pTorso || p == pArm {
			t.Errorf("u=%v chose part %d", u, p)
		}
	}
}
