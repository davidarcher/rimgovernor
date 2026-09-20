package policy

import (
	"errors"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type GearSlot string

const (
	GearSkinTorso   GearSlot = "skin-torso"
	GearSkinLegs    GearSlot = "skin-legs"
	GearMiddleTorso GearSlot = "middle-torso"
	GearOuter       GearSlot = "outer"
	GearBelt        GearSlot = "belt"
	GearHeadgear    GearSlot = "headgear"
	GearPrimary     GearSlot = "primary"
)

var gearSlots = []GearSlot{GearSkinTorso, GearSkinLegs, GearMiddleTorso, GearOuter, GearBelt, GearHeadgear, GearPrimary}

type GearRole string

const (
	GearWorker       GearRole = "worker"
	GearSoldier      GearRole = "soldier"
	GearHunter       GearRole = "hunter"
	GearIndoor       GearRole = "crafter/indoor"
	GearChild        GearRole = "child"
	GearSlave        GearRole = "slave"
	GearNonCombatant GearRole = "non-combatant"
)

// GearRoleInput reuses the work planner's priorities, skills and trait facts.
// Explicit social/developmental status and squad membership take precedence.
type GearRoleInput struct {
	Work                                            WorkPawn
	Child, Slave, IncapableOfViolence, DraftedSquad bool
}

func DeriveGearRole(p GearRoleInput) GearRole {
	if p.Child {
		return GearChild
	}
	if p.Slave {
		return GearSlave
	}
	incapable, _ := p.Work.Incapable.Value()
	for _, w := range incapable {
		if w == "Violent" {
			p.IncapableOfViolence = true
		}
	}
	if p.IncapableOfViolence {
		return GearNonCombatant
	}
	if p.DraftedSquad {
		return GearSoldier
	}
	priorities, _ := p.Work.Work.Value()
	best := 5
	role := GearWorker
	for _, w := range priorities {
		if w.Disabled || w.Priority <= 0 || w.Priority > 4 {
			continue
		}
		r := GearWorker
		switch w.Work {
		case WorkHunting:
			r = GearHunter
		case WorkCrafting, WorkTailoring, WorkSmithing, WorkResearch, WorkArt:
			r = GearIndoor
		}
		if w.Priority < best || w.Priority == best && r < role {
			best, role = w.Priority, r
		}
	}
	return role
}

type GearSource string

const (
	GearWorn       GearSource = "worn"
	GearLoose      GearSource = "loose"
	GearStored     GearSource = "stored"
	GearBillSource GearSource = "bill"
)

// Stats are for this def x stuff at Normal quality, before condition. The
// provider resolves native material factors, eligibility and production gates;
// the model never invents materials, recipes or modded definition stats.
type GearOption struct {
	ID                                                          string
	Definition, Stuff                                           Resource
	Quality                                                     int // Awful=0 through Legendary=6
	Slot                                                        GearSlot
	Layers, Groups                                              []string
	Source                                                      GearSource
	Condition, Sharp, Blunt, Cold, Heat, MoveSpeed, Cost, Range float64
	Tainted, Locked, Shield, Ranged                             bool
	// Psychic marks a psychic foil helmet, Smokepop a smokepop belt: utility
	// gear the model plans only on evidence (a psychic-drone letter) or never.
	Psychic, Smokepop bool
	// Research names the ResearchProjectDefs the option's recipe requires;
	// Ingredients its materials for a bill source (the armor ladder's steel,
	// plasteel and components), judged against the input Budget.
	Research    []string
	Ingredients []Amount
}

type GearLoadoutInput struct {
	Climate                                      *GearClimate
	Role                                         GearRoleInput
	Female, Nudist, Bloodlust, Inhuman, Smithing bool
	// Research is the finished research census; an option is eligible only
	// once every project its Research names is finished (Smithing remains
	// the legacy flag for the simple helmet).
	Research []string
	// Budget is GearMaterialBudget: stock after MaintainResource reserves.
	// A bill option whose Ingredients exceed it is refused; nil is
	// unbudgeted.
	Budget []Amount
	// PsychicDrone is whether a psychic-drone letter has been seen.
	PsychicDrone                            bool
	Ambient, ComfortableMin, ComfortableMax float64
	Worn                                    []GearOption
	// Options have passed outfit/body/stage and resource-policy eligibility.
	// Loose/stored IDs identify physical items; bill IDs identify products.
	Options []GearOption
}

type GearGap struct {
	Slot   GearSlot
	Wanted GearOption
	Source GearSource
	Gain   float64
}

type GearLoadout struct {
	Pawn   PawnID
	Role   GearRole
	Target []GearOption
	Gaps   []GearGap
	Score  float64
}

type GearDemand struct {
	Definition, Stuff Resource
	Count             int
}

func GearQualityMultipliers(quality int) (armor, insulation float64) {
	if quality < 0 || quality > 6 {
		return 0, 0
	}
	return [...]float64{.6, .8, 1, 1.15, 1.3, 1.45, 1.8}[quality], [...]float64{.8, .9, 1, 1.1, 1.2, 1.5, 1.8}[quality]
}

func GearGapThreshold(role GearRole) float64 {
	switch role {
	case GearSoldier:
		return .05
	case GearHunter:
		return .1
	case GearSlave:
		return .5
	default:
		return .2
	}
}

func gearMelee(p GearLoadoutInput) bool {
	traits, _ := p.Role.Work.Traits.Value()
	for _, t := range traits {
		if TraitEffect(t).MeleeOnly {
			return true
		}
	}
	skills, _ := p.Role.Work.Skills.Value()
	melee, shooting := 0, 0
	for _, s := range skills {
		if !s.Disabled {
			if s.Name == "Melee" {
				melee = s.Level
			}
			if s.Name == "Shooting" {
				shooting = s.Level
			}
		}
	}
	return melee > shooting
}

// GearArmorSpeedFloor is the move-speed penalty at or past which the soldier
// role refuses armor: plate (-0.8) and cataphract (-0.5) slow a squad more
// than their armor is worth; marine (-0.25) and flak vests (-0.12) pass.
const GearArmorSpeedFloor = -.5

func gearResearched(p GearLoadoutInput, project string) bool {
	for _, finished := range p.Research {
		if finished == project {
			return true
		}
	}
	return false
}

// gearMedic reports a pawn whose highest enabled work priority is doctoring.
func gearMedic(p GearLoadoutInput) bool {
	priorities, _ := p.Role.Work.Work.Value()
	best, medic := 5, false
	for _, w := range priorities {
		if w.Disabled || w.Priority <= 0 || w.Priority > 4 {
			continue
		}
		if w.Priority < best {
			best, medic = w.Priority, w.Work == WorkDoctor
		} else if w.Priority == best && w.Work != WorkDoctor {
			medic = false
		}
	}
	return medic
}

func gearEligible(p GearLoadoutInput, o GearOption) bool {
	role := DeriveGearRole(p.Role)
	kid := strings.HasPrefix(string(o.Definition), "Kid") || strings.HasPrefix(string(o.Definition), "Apparel_Kid")
	if (role == GearChild) != kid {
		return false
	}
	// A shield belt stops the wearer shooting: melee soldiers and the medic.
	if o.Shield && !(role == GearSoldier && gearMelee(p) || role != GearSoldier && role != GearChild && role != GearSlave && gearMedic(p)) {
		return false
	}
	if o.Smokepop || o.Psychic && !p.PsychicDrone {
		return false
	}
	if role == GearSoldier && o.Slot == GearHeadgear && o.Sharp > 0 && !p.Smithing && !gearResearched(p, "Smithing") {
		return false
	}
	// Plate (-0.8 c/s) and cataphract (-0.5) never; recon and marine only as
	// far as their plasteel and advanced components are funded.
	if role == GearSoldier && o.MoveSpeed <= GearArmorSpeedFloor {
		return false
	}
	for _, project := range o.Research {
		if !gearResearched(p, project) {
			return false
		}
	}
	if o.Source == GearBillSource && !gearFunded(p.Budget, o) {
		return false
	}
	if o.Slot == GearPrimary {
		if role == GearNonCombatant || role == GearChild {
			return false
		}
		if role == GearHunter && (!o.Ranged || o.Range < 25) {
			return false
		}
		if gearMelee(p) && o.Ranged {
			return false
		}
	}
	if (role == GearWorker || role == GearIndoor || role == GearNonCombatant) && o.MoveSpeed < 0 {
		return false
	}
	return true
}

// Conflicts require a shared layer AND a shared body group. The primary
// weapon and each logical slot are exclusive independently of apparel layers.
func GearConflicts(a, b GearOption) bool {
	if a.Slot == b.Slot {
		return true
	}
	for _, l := range a.Layers {
		for _, m := range b.Layers {
			if l == m {
				for _, g := range a.Groups {
					for _, h := range b.Groups {
						if g == h {
							return true
						}
					}
				}
			}
		}
	}
	return false
}

func gearItemScore(p GearLoadoutInput, o GearOption) float64 {
	a, i := GearQualityMultipliers(o.Quality)
	role := DeriveGearRole(p.Role)
	armor := (o.Sharp*2 + o.Blunt) * a * o.Condition
	low, high := p.Climate.TemperatureRange(p.Ambient)
	thermal := math.Min(math.Max(0, p.ComfortableMin-low), o.Cold*i) + math.Min(math.Max(0, high-p.ComfortableMax), o.Heat*i)
	score := armor + thermal + o.MoveSpeed*10 - o.Cost*.001
	switch role {
	case GearSoldier:
		score += armor * 9
	case GearHunter:
		if o.Slot == GearPrimary {
			score += o.Range
		}
		if o.Slot == GearOuter {
			score += o.Cold * i * .2
		}
	case GearIndoor:
		score -= thermal * .8
	case GearSlave:
		score = -o.Cost + thermal*.01
	}
	if o.Condition <= GearTatteredCondition {
		score -= 20
	}
	return score
}

func gearEnsembleScore(p GearLoadoutInput, items []GearOption) float64 {
	score, tainted := 0.0, 0
	legs, chest, dressed := false, false, false
	for _, o := range items {
		score += gearItemScore(p, o)
		if o.Tainted {
			tainted++
		}
		for _, g := range o.Groups {
			legs = legs || g == "Legs"
			chest = chest || g == "Torso"
		}
		dressed = dressed || o.Slot != GearBelt && o.Slot != GearHeadgear && o.Slot != GearPrimary
	}
	if !p.Bloodlust && !p.Inhuman && tainted > 0 {
		score -= float64(5 + 3*(min(tainted, 4)-1))
	}
	if p.Nudist {
		if dressed {
			score -= 30
		}
	} else {
		// Coverage dominates optional upgrades but remains a scored need.
		if !legs {
			score -= 1000
		}
		if p.Female && !chest {
			score -= 1000
		}
	}
	return score
}

func (p GearLoadoutInput) Validate() error {
	if err := p.Climate.Validate(); err != nil {
		return err
	}
	if len(p.Worn) > 7 || len(p.Options) > 64 {
		return errors.New("gear loadout exceeds bound")
	}
	for _, n := range []float64{p.Ambient, p.ComfortableMin, p.ComfortableMax} {
		if math.IsNaN(n) || math.IsInf(n, 0) {
			return errors.New("invalid gear temperature")
		}
	}
	if p.ComfortableMin > p.ComfortableMax {
		return errors.New("invalid gear comfort band")
	}
	ids := map[string]bool{}
	for _, o := range append(append([]GearOption{}, p.Worn...), p.Options...) {
		if !foodID(o.ID) || ids[o.ID] || !validResource(o.Definition) || o.Stuff != "" && !validResource(o.Stuff) || o.Quality < 0 || o.Quality > 6 {
			return errors.New("invalid gear option identity")
		}
		ids[o.ID] = true
		slot := false
		for _, s := range gearSlots {
			slot = slot || o.Slot == s
		}
		if !slot {
			return errors.New("invalid gear slot")
		}
		if o.Source != GearWorn && o.Source != GearLoose && o.Source != GearStored && o.Source != GearBillSource {
			return errors.New("invalid gear source")
		}
		for _, n := range []float64{o.Condition, o.Sharp, o.Blunt, o.Cold, o.Heat, o.Cost, o.Range} {
			if !foodNumber(n) {
				return errors.New("invalid gear stat")
			}
		}
		if o.Condition > 1 || math.IsNaN(o.MoveSpeed) || math.IsInf(o.MoveSpeed, 0) {
			return errors.New("invalid gear condition or speed")
		}
		if len(o.Research) > 16 || len(o.Ingredients) > 16 {
			return errors.New("gear recipe exceeds bound")
		}
		for _, project := range o.Research {
			if !validResource(Resource(project)) {
				return errors.New("invalid gear research")
			}
		}
		for _, a := range o.Ingredients {
			if !validResource(a.Resource) || a.Count <= 0 {
				return errors.New("invalid gear ingredient")
			}
		}
		for _, list := range [][]string{o.Layers, o.Groups} {
			if len(list) > 16 {
				return errors.New("gear coverage exceeds bound")
			}
			seen := map[string]bool{}
			for _, v := range list {
				if !foodID(v) || seen[v] {
					return errors.New("invalid gear coverage")
				}
				seen[v] = true
			}
		}
	}
	if len(p.Research) > 512 || len(p.Budget) > 64 {
		return errors.New("gear research or budget exceeds bound")
	}
	for _, project := range p.Research {
		if !validResource(Resource(project)) {
			return errors.New("invalid gear research")
		}
	}
	for _, a := range p.Budget {
		if !validResource(a.Resource) || a.Count < 0 {
			return errors.New("invalid gear budget")
		}
	}
	for _, o := range p.Options {
		if o.Source == GearWorn || o.Locked {
			return errors.New("invalid unworn gear option")
		}
	}
	for i, o := range p.Worn {
		if o.Source != GearWorn {
			return errors.New("invalid worn gear source")
		}
		for _, other := range p.Worn[:i] {
			if GearConflicts(o, other) {
				return errors.New("conflicting worn gear")
			}
		}
	}
	return nil
}

// PlanGearLoadout searches compatible ensembles, retaining forced/locked items.
// Equal scores retain worn gear, then stable item identity. No input is mutated.
func PlanGearLoadout(p GearLoadoutInput) (GearLoadout, error) {
	if err := p.Validate(); err != nil {
		return GearLoadout{}, err
	}
	traits, _ := p.Role.Work.Traits.Value()
	for _, t := range traits {
		p.Nudist = p.Nudist || TraitEffect(t).Nudist
		p.Bloodlust = p.Bloodlust || t.Name == "Bloodlust"
		p.Inhuman = p.Inhuman || t.Name == "Inhuman"
	}
	role := DeriveGearRole(p.Role)
	options := append(append([]GearOption{}, p.Worn...), p.Options...)
	sort.SliceStable(options, func(i, j int) bool {
		rank := map[GearSource]int{GearWorn: 0, GearLoose: 1, GearStored: 2, GearBillSource: 3}
		if rank[options[i].Source] != rank[options[j].Source] {
			return rank[options[i].Source] < rank[options[j].Source]
		}
		return options[i].ID < options[j].ID
	})
	bySlot := map[GearSlot][]GearOption{}
	var locked []GearOption
	for _, o := range options {
		if o.Locked {
			locked = append(locked, o)
		}
		if o.Source == GearWorn || gearEligible(p, o) {
			bySlot[o.Slot] = append(bySlot[o.Slot], o)
		}
	}
	best := append([]GearOption{}, p.Worn...)
	bestScore := gearEnsembleScore(p, best)
	// All ensemble adjustments are penalties, so remaining positive item
	// scores form an admissible upper bound for pruning the exact search.
	upper := make([]float64, len(gearSlots)+1)
	for i := len(gearSlots) - 1; i >= 0; i-- {
		m := 0.0
		for _, o := range bySlot[gearSlots[i]] {
			m = math.Max(m, gearItemScore(p, o))
		}
		upper[i] = upper[i+1] + m
	}
	var visit func(int, []GearOption, float64)
	visit = func(index int, chosen []GearOption, raw float64) {
		if raw+upper[index] < bestScore {
			return
		}
		if index == len(gearSlots) {
			// This slice proposes purchases/replacements, not strip orders.
			// Do not credit recovery for silently removing a worn garment.
			for _, w := range p.Worn {
				covered := false
				for _, c := range chosen {
					covered = covered || GearConflicts(w, c)
				}
				if !covered {
					return
				}
			}
			score := gearEnsembleScore(p, chosen)
			if score > bestScore || score == bestScore && gearPurchases(chosen) < gearPurchases(best) {
				bestScore = score
				best = append([]GearOption{}, chosen...)
			}
			return
		}
		slot := gearSlots[index]
		forced := false
		for _, l := range locked {
			forced = forced || l.Slot == slot
		}
		for _, o := range bySlot[slot] {
			ok := true
			for _, l := range locked {
				if l.ID != o.ID && GearConflicts(l, o) {
					ok = false
				}
			}
			for _, c := range chosen {
				if GearConflicts(c, o) {
					ok = false
				}
			}
			if ok {
				visit(index+1, append(chosen, o), raw+gearItemScore(p, o))
			}
		}
		if !forced {
			visit(index+1, chosen, raw)
		}
	}
	visit(0, nil, 0)
	out := GearLoadout{Role: role, Target: best, Score: bestScore}
	for _, o := range best {
		if o.Source != GearWorn {
			// Compare the ensemble with this slot left as currently worn.
			alternative := []GearOption{}
			for _, other := range best {
				if other.ID != o.ID {
					alternative = append(alternative, other)
				}
			}
			for _, worn := range p.Worn {
				if !GearConflicts(worn, o) {
					continue
				}
				fits := true
				for _, other := range alternative {
					if GearConflicts(worn, other) {
						fits = false
					}
				}
				if fits {
					alternative = append(alternative, worn)
				}
			}
			out.Gaps = append(out.Gaps, GearGap{Slot: o.Slot, Wanted: o, Source: o.Source, Gain: bestScore - gearEnsembleScore(p, alternative)})
		}
	}
	sort.Slice(out.Gaps, func(i, j int) bool {
		if out.Gaps[i].Gain != out.Gaps[j].Gain {
			return out.Gaps[i].Gain > out.Gaps[j].Gain
		}
		return out.Gaps[i].Slot < out.Gaps[j].Slot
	})
	return out, nil
}

func gearPurchases(items []GearOption) int {
	n := 0
	for _, o := range items {
		if o.Source != GearWorn {
			n++
		}
	}
	return n
}

// modeledGearObservation projects model-selected gaps into the existing method
// planner. Native gain is only an admission gate: a loose/stored item must still
// occur in the fresh eligible candidate list, but is ordered by model gain.
func modeledGearObservation(v GearObservation, loadouts []GearLoadout) GearObservation {
	if len(loadouts) == 0 {
		return v
	}
	v.Pawns = append([]GearPawn{}, v.Pawns...)
	models := map[PawnID]GearLoadout{}
	for _, l := range loadouts {
		models[l.Pawn] = l
	}
	for i, p := range v.Pawns {
		l, ok := models[p.Pawn]
		if !ok {
			continue
		}
		native, _ := p.Candidates.Value()
		candidates := []GearCandidate{}
		needs := []GearReplacement{}
		deficit := false
		for _, g := range l.Gaps {
			if g.Gain <= GearGapThreshold(l.Role) {
				continue
			}
			deficit = true
			if g.Source == GearBillSource {
				needs = append(needs, GearReplacement{Definition: g.Wanted.Definition, Stuff: g.Wanted.Stuff, Reason: "loadout"})
				continue
			}
			for _, c := range native {
				if c.Target == g.Wanted.ID && c.Definition == g.Wanted.Definition && c.Gain > 0 {
					c.Gain = g.Gain
					candidates = append(candidates, c)
				}
			}
		}
		p.Deficit = domain.Known(deficit)
		p.Candidates = domain.Known(candidates)
		p.Replacements = domain.Known(needs)
		v.Pawns[i] = p
	}
	return v
}

// GearProductionDemand counts purchases, not unique definitions per pawn;
// quality does not split a native def x stuff production bill.
func GearProductionDemand(loadouts []GearLoadout) []GearDemand {
	type key struct{ def, stuff Resource }
	counts := map[key]int{}
	for _, l := range loadouts {
		for _, g := range l.Gaps {
			if g.Source == GearBillSource && g.Gain > GearGapThreshold(l.Role) {
				counts[key{g.Wanted.Definition, g.Wanted.Stuff}]++
			}
		}
	}
	out := []GearDemand{}
	for k, n := range counts {
		out = append(out, GearDemand{k.def, k.stuff, n})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Definition != out[j].Definition {
			return out[i].Definition < out[j].Definition
		}
		return out[i].Stuff < out[j].Stuff
	})
	return out
}

// PlanColonyGear allocates physical supplies once in stable pawn order. Bill
// options remain reusable; their resulting quantities are aggregated separately.
func PlanColonyGear(pawns []GearPawn) ([]GearLoadout, domain.Fact[[]GearDemand], error) {
	seen := map[PawnID]bool{}
	for _, p := range pawns {
		if !foodID(string(p.Pawn)) || seen[p.Pawn] {
			return nil, domain.Unknown[[]GearDemand](), errors.New("invalid gear pawn identity")
		}
		seen[p.Pawn] = true
	}
	rows := append([]GearPawn{}, pawns...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Pawn < rows[j].Pawn })
	used := map[string]bool{}
	out := []GearLoadout{}
	for _, p := range rows {
		input, known := p.LoadoutModel.Value()
		if !known {
			return nil, domain.Unknown[[]GearDemand](), nil
		}
		if p.Climate != nil {
			input.Climate = p.Climate
		}
		options := []GearOption{}
		for _, o := range input.Options {
			if o.Source == GearBillSource || !used[o.ID] {
				options = append(options, o)
			}
		}
		input.Options = options
		l, err := PlanGearLoadout(input)
		if err != nil {
			return nil, domain.Unknown[[]GearDemand](), err
		}
		l.Pawn = p.Pawn
		out = append(out, l)
		for _, g := range l.Gaps {
			if g.Source != GearBillSource && g.Gain > GearGapThreshold(l.Role) {
				used[g.Wanted.ID] = true
			}
		}
	}
	return out, domain.Known(GearProductionDemand(out)), nil
}
