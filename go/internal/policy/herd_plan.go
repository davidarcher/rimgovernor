package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HerdJob is the one reason the colony keeps a race (#1628).
type HerdJob string

const (
	HerdJobMilk      HerdJob = "milk"
	HerdJobWool      HerdJob = "wool"
	HerdJobChemfuel  HerdJob = "chemfuel"
	HerdJobFood      HerdJob = "food" // eggs
	HerdJobHaul      HerdJob = "haul"
	HerdJobWar       HerdJob = "war"
	HerdJobCompanion HerdJob = "companion"
	HerdJobNone      HerdJob = "none"
)

// herdWorkJobs are the jobs a race earns by yield or training, in the order a
// race's label is chosen.
var herdWorkJobs = []HerdJob{HerdJobMilk, HerdJobWool, HerdJobChemfuel, HerdJobFood, HerdJobHaul, HerdJobWar}

// Trainables that make a trained job (RimWorld's TrainableDefs: "Release" is
// the attack training).
const (
	herdHaulTraining = "Haul"
	herdWarTraining  = "Release"
)

// HerdPlanInput is every fact the herd plan derives from. Unknown facts leave
// the plan unchanged: a race whose yield, size or capacity is unread cannot
// hold or be ranked for the job that needs it. The catalog is required.
type HerdPlanInput struct {
	Animals, Wild domain.Fact[[]UpkeepAnimal]
	Races         AnimalRaceCatalog
	Budget        domain.Fact[float64]
	Wealth        domain.Fact[WealthFacts]
	Pens          domain.Fact[[]PenGrazing]
	Food          domain.Fact[FoodPlan]
	// Offers are races a trader offers or a quest rewards now; they count
	// as obtainable (the buying and reward hooks, #1636).
	Offers []Resource
	// Handlers are the colony's pawn profiles; an unread roster plans no
	// leveling.
	Handlers domain.Fact[[]PawnProfile]
}

// HerdPlanInput is the plan's facts from a routine reading.
func (f RoutineFacts) HerdPlanInput() HerdPlanInput {
	return HerdPlanInput{Animals: f.AnimalUpkeep.Animals, Wild: f.AnimalUpkeep.WildAnimals, Races: f.AnimalUpkeep.AnimalRaces,
		Budget: f.WealthBudget(), Wealth: f.Wealth, Pens: f.PenGrazing, Food: f.FoodPlan, Handlers: f.WorkProfiles}
}

// HerdPolicy is the population band of the herd plan of f.
func (f RoutineFacts) HerdPolicy() HerdPolicy { return PlanHerd(f.HerdPlanInput()).Policy }

// PenAnimals is the herd the layout's pens, barn and vet room are sized for.
func (f RoutineFacts) PenAnimals() int { return PlanHerd(f.HerdPlanInput()).PenAnimals() }

// HerdRole is one race's place in the plan. Consumers (taming, training,
// sterilize, sell, buy, pens) read it; none of them owns a second copy.
type HerdRole struct {
	Race Resource
	// Job is the race's job: the first work job it keeps, else retiring
	// work, else companion (a bonded animal), else none.
	Job HerdJob
	// Preferred is the plan's pick for at least one job.
	Preferred bool
	// Founder marks a preferred race that has animals but no breeding pair:
	// they are never culled or sold, WantMale/WantFemale name the missing
	// sex (taming and buying), and the race has no cap.
	Founder              bool
	WantMale, WantFemale bool
	// Retiring marks a race every one of whose jobs a better, adult,
	// paired race now covers: surplus, no breeding pair kept.
	Retiring bool
	// Superseded marks a race kept only for jobs a better race will take
	// over but does not yet cover: it keeps working and stops breeding
	// (sterilize) until it retires.
	Superseded bool
}

// HerdJobPlan is one job's ranking.
type HerdJobPlan struct {
	Job HerdJob
	// Ranked are the obtainable races, best first; Target is the first.
	Ranked []Resource
	Target Resource
	// Heads is the target race's wanted head count.
	Heads int64
}

// HerdPlan is the derived plan, recomputed every cycle and never stored.
type HerdPlan struct {
	Roles  map[Resource]HerdRole
	Jobs   map[HerdJob]HerdJobPlan
	Policy HerdPolicy
	// Leveling is the easy race tamed (and trained) to raise the best
	// handler's Animals skill while a wanted race needs more than any
	// handler has (#1634); empty when none is needed or available.
	Leveling Resource
}

type herdRace struct {
	race AnimalRace
	// males, females and asexual count fertile animals only: they make the pair.
	n, males, females, asexual int64
	unsexed                    bool
	bonded                     bool
	// capacity is the summed per-adult capacity by job over adult animals
	// holding it; holds is whether any animal holds the job.
	capacity map[HerdJob]float64
	holds    map[HerdJob]bool
}

func (r *herdRace) paired() bool {
	if r.unsexed {
		return false
	}
	return r.asexual > 0 || r.males > 0 && r.females > 0
}

// herdProductJob maps a race product to the job it serves.
func herdProductJob(p RaceProduct) (HerdJob, bool) {
	switch {
	case p.Kind == "milk":
		return HerdJobMilk, true
	case p.Kind == "wool":
		return HerdJobWool, true
	case p.Kind == "eggs":
		return HerdJobFood, true
	case p.Kind == "spawner" && p.Def == "Chemfuel":
		return HerdJobChemfuel, true
	}
	return "", false
}

// herdPerAdult is what one adult of the race gives a job per day (products)
// or in total (carrying capacity, combat power). capable is whether the race
// can hold the job at all; known whether its yield is read and positive.
func herdPerAdult(job HerdJob, r AnimalRace) (value float64, capable, known bool) {
	switch job {
	case HerdJobHaul:
		v, ok := r.CarryingCapacity.Value()
		return v, slices.Contains(r.Trainables, herdHaulTraining), ok && v > 0
	case HerdJobWar:
		v, ok := r.CombatPower.Value()
		return v, slices.Contains(r.Trainables, herdWarTraining), ok && v > 0
	}
	known = true
	for _, p := range r.Products {
		if j, ok := herdProductJob(p); !ok || j != job {
			continue
		}
		capable = true
		amount, ak := p.Amount.Value()
		interval, ik := p.IntervalDays.Value()
		if !ak || !ik || interval <= 0 {
			known = false
			continue
		}
		value += amount / interval
	}
	return value, capable, known && value > 0
}

// herdScore is yield per unit of feed, body size standing for the feed an
// animal eats.
func herdScore(job HerdJob, r AnimalRace) (float64, bool) {
	value, _, known := herdPerAdult(job, r)
	size, sk := r.BodySize.Value()
	if !known || !sk || size <= 0 {
		return 0, false
	}
	return value / size, true
}

func herdLearned(a UpkeepAnimal, def string) bool {
	for _, t := range a.Training {
		if learned, ok := t.Learned.Value(); ok && learned && t.Def == def {
			return true
		}
	}
	return false
}

// herdHeld is whether the animal holds the job, and what it adds when adult.
func herdHeld(job HerdJob, a UpkeepAnimal, race AnimalRace) (holds bool, value float64) {
	per, capable, _ := herdPerAdult(job, race)
	if _, known := herdScore(job, race); !capable || !known {
		return false, 0
	}
	switch job {
	case HerdJobHaul:
		holds = herdLearned(a, herdHaulTraining)
	case HerdJobWar:
		holds = herdLearned(a, herdWarTraining)
	default:
		holds = true
	}
	if holds && !herdJuvenile(a) {
		value = per
	}
	return holds, value
}

// herdBudgetShare is the share of the race budget the defense can hold: set
// only while the wealth budget has negative headroom.
func herdBudgetShare(headroom, total float64) (float64, bool) {
	if headroom >= 0 || total <= 0 {
		return 0, false
	}
	return math.Max(0, (total+headroom)/total), true
}

// herdSpare is the breeding stock kept beyond a job's head count.
const herdSpare = herdPairSize

// herdUnplannedFloor is the smallest ceiling the wealth budget cuts a race
// without a job to.
const herdUnplannedFloor = 6

// PlanHerd derives each race's job from the colony facts (#1628). A job is
// wanted while any kept animal holds it: a yield (milk, wool, chemfuel,
// eggs) or a learned training (haul, war). Its candidates are the catalog
// races able to hold it that the colony can obtain (owned, tameable wild,
// offered), ranked by yield per body size, then milk to cows, then lower
// handling skill. The best is the target: the plan wants its head count. A
// holder that is not the target retires once the target is adult, has a
// breeding pair and its adult capacity covers the holder's; until then the
// holder keeps working. Wealth and pasture bound the result as ceilings.
func PlanHerd(in HerdPlanInput) HerdPlan {
	plan := HerdPlan{Roles: map[Resource]HerdRole{}, Jobs: map[HerdJob]HerdJobPlan{},
		Policy: HerdPolicy{PopulationMin: map[Resource]int64{}, PopulationMax: map[Resource]int64{}}}
	floors := FoodHerdPolicy(HerdPolicy{}, in.Food).PopulationMin
	rows, rk := in.Animals.Value()
	if !rk {
		return plan
	}
	stats := map[Resource]*herdRace{}
	var order []Resource
	for _, a := range rows {
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if rk && release || sk && slaughter {
			continue
		}
		s := stats[a.Definition]
		if s == nil {
			s = &herdRace{capacity: map[HerdJob]float64{}, holds: map[HerdJob]bool{}}
			stats[a.Definition], order = s, append(order, a.Definition)
			s.race, _ = in.Races.Race(a.Definition)
		}
		s.n++
		switch {
		case a.Gender != "Male" && a.Gender != "Female" && a.Gender != "None":
			s.unsexed = true
		case !herdFertile(a): // a sterilized animal is no part of a pair
		case a.Gender == "Male":
			s.males++
		case a.Gender == "Female":
			s.females++
		default:
			s.asexual++
		}
		if bonded, _ := a.Bonded.Value(); bonded {
			s.bonded = true
		}
		for _, job := range herdWorkJobs {
			if holds, value := herdHeld(job, a, s.race); holds {
				s.holds[job] = true
				s.capacity[job] += value
			}
		}
	}
	sort.Slice(order, func(i, j int) bool { return order[i] < order[j] })
	obtainable := herdObtainable(in, stats)
	retiring := map[Resource]bool{}
	preferred := map[Resource]bool{}
	keeps := map[Resource]map[HerdJob]bool{}
	superseded := map[Resource]bool{}
	targets := map[Resource]int64{}
	for _, job := range herdWorkJobs {
		var holders []Resource
		for _, def := range order {
			if stats[def].holds[job] {
				holders = append(holders, def)
			}
		}
		if len(holders) == 0 && !(job == HerdJobWar && herdWarWanted(in)) {
			continue
		}
		jp := HerdJobPlan{Job: job, Ranked: herdRank(job, in.Races, herdCandidates(job, in, stats, obtainable))}
		if len(jp.Ranked) == 0 {
			if len(holders) == 0 {
				continue
			}
			for _, def := range holders {
				herdKeep(keeps, def, job)
			}
			plan.Jobs[job] = jp
			continue
		}
		jp.Target = jp.Ranked[0]
		preferred[jp.Target] = true
		herdKeep(keeps, jp.Target, job)
		target := stats[jp.Target]
		if target == nil {
			target = &herdRace{capacity: map[HerdJob]float64{}}
		}
		targetRace, _ := in.Races.Race(jp.Target)
		perTarget, _, _ := herdPerAdult(job, targetRace)
		demand := target.capacity[job]
		others, foodDemand := 0.0, 0.0
		for _, def := range holders {
			s := stats[def]
			per, _, _ := herdPerAdult(job, s.race)
			foodDemand += float64(floors[def]) * per
			if def == jp.Target {
				continue
			}
			others += s.capacity[job]
			if target.paired() && target.capacity[job] > 0 && target.capacity[job] >= s.capacity[job] {
				continue
			}
			herdKeep(keeps, def, job)
			superseded[def] = true
		}
		demand = math.Max(demand, math.Max(others, foodDemand))
		jp.Heads = herdPairSize
		if !math.IsInf(demand, 0) {
			jp.Heads = max(jp.Heads, int64(math.Ceil(demand/perTarget)))
		}
		targets[jp.Target] = max(targets[jp.Target], jp.Heads)
		plan.Jobs[job] = jp
	}
	for _, def := range order {
		s := stats[def]
		role := HerdRole{Race: def, Job: HerdJobNone, Preferred: preferred[def], Superseded: superseded[def] && !preferred[def]}
		var held []HerdJob
		for _, job := range herdWorkJobs {
			if s.holds[job] {
				held = append(held, job)
			}
		}
		for _, job := range herdWorkJobs {
			if keeps[def][job] {
				role.Job = job
				break
			}
		}
		switch {
		case len(held) > 0 && len(keeps[def]) == 0:
			role.Retiring, role.Job = true, held[0]
			retiring[def] = true
		case role.Job == HerdJobNone && s.bonded:
			role.Job = HerdJobCompanion
		}
		if role.Preferred && s.n > 0 && !s.unsexed && !s.paired() {
			role.Founder = true
			role.WantMale, role.WantFemale = s.males == 0, s.females == 0
		}
		plan.Roles[def] = role
	}
	for def, heads := range targets {
		if _, owned := stats[def]; !owned {
			plan.Roles[def] = HerdRole{Race: def, Job: herdTargetJob(plan.Jobs, def), Preferred: true, Founder: false, WantMale: true, WantFemale: true}
		}
		plan.Policy.PopulationMin[def] = heads
		plan.Policy.PopulationMax[def] = heads + herdSpare
	}
	if race := herdLevelingRace(in, stats, targets); race != "" {
		plan.Leveling = race
		plan.Roles[race] = HerdRole{Race: race, Job: HerdJobNone}
		plan.Policy.PopulationMin[race] = 1
		plan.Policy.PopulationMax[race] = 1 + herdSpare
	}
	if share, ok := herdBudgetCeiling(in); ok {
		for def, ceiling := range plan.Policy.PopulationMax {
			plan.Policy.PopulationMax[def] = herdShareOf(ceiling, share, herdPairSize)
			plan.Policy.PopulationMin[def] = min(plan.Policy.PopulationMin[def], plan.Policy.PopulationMax[def])
		}
		for _, def := range order {
			if _, planned := targets[def]; !planned && !retiring[def] && plan.Roles[def].Job != HerdJobCompanion {
				plan.Policy.PopulationMax[def] = herdShareOf(stats[def].n, share, herdUnplannedFloor)
			}
		}
	}
	if ratio, known := herdPastureRatio(in.Pens); known {
		plan.Policy.FeedShort = ratio < 1
		penned := map[Resource]int64{}
		for _, a := range rows {
			if pen, ok := a.RequiresPen.Value(); ok && pen && !a.Herd.Predator {
				penned[a.Definition]++
			}
		}
		for def, n := range penned {
			feed := int64(math.Floor(float64(n) * ratio))
			if ceiling, ok := plan.Policy.PopulationMax[def]; !ok || feed < ceiling {
				plan.Policy.PopulationMax[def] = feed
			}
		}
	}
	plan.Policy.Roles = plan.Roles
	for _, def := range order {
		if layer, ok := herdLayerOf(stats[def].race); ok {
			if plan.Policy.Layers == nil {
				plan.Policy.Layers = map[Resource]HerdLayer{}
			}
			plan.Policy.Layers[def] = layer
		}
	}
	for def, role := range plan.Roles {
		switch {
		case role.Retiring:
			plan.Policy.PopulationMax[def] = 0
			delete(plan.Policy.PopulationMin, def)
			if plan.Policy.Retired == nil {
				plan.Policy.Retired = map[Resource]bool{}
			}
			plan.Policy.Retired[def] = true
		case role.Founder, role.Job == HerdJobCompanion:
			delete(plan.Policy.PopulationMax, def)
		}
	}
	return plan
}

// herdLevelingRace is the easy race to tame while a wanted race's head
// count is short and its minimum handling skill is above what every handler
// has (TamerFor). It is the quickest leveller among the tameable wild races
// on the map that a handler already clears: lowest minimum handling skill,
// then lowest wildness, both read from the race catalog (an unread wildness
// ranks last), then definition name. Empty once a handler clears the wanted
// race (the leveling stops) or no such animal is on the map.
func herdLevelingRace(in HerdPlanInput, stats map[Resource]*herdRace, targets map[Resource]int64) Resource {
	profiles, ok := in.Handlers.Value()
	if !ok {
		return ""
	}
	gap := false
	for def, heads := range targets {
		race, _ := in.Races.Race(def)
		minimum, known := race.MinimumHandlingSkill.Value()
		if !known || stats[def] != nil && stats[def].n >= heads {
			continue
		}
		if _, handled := TamerFor(profiles, minimum); !handled {
			gap = true
		}
	}
	if !gap {
		return ""
	}
	wild, _ := in.Wild.Value()
	best, bestSkill, bestWild := Resource(""), 0, 0.0
	for _, a := range wild {
		if tameable, ok := a.Tameable.Value(); !ok || !tameable || herdDangerous(a) {
			continue
		}
		race, _ := in.Races.Race(a.Definition)
		minimum, known := race.MinimumHandlingSkill.Value()
		if _, handled := TamerFor(profiles, minimum); !known || !handled {
			continue
		}
		wildness, wk := race.Wildness.Value()
		if !wk {
			wildness = math.MaxFloat64
		}
		if best == "" || minimum < bestSkill || minimum == bestSkill && (wildness < bestWild || wildness == bestWild && a.Definition < best) {
			best, bestSkill, bestWild = a.Definition, minimum, wildness
		}
	}
	return best
}

func herdKeep(keeps map[Resource]map[HerdJob]bool, def Resource, job HerdJob) {
	if keeps[def] == nil {
		keeps[def] = map[HerdJob]bool{}
	}
	keeps[def][job] = true
}

func herdTargetJob(jobs map[HerdJob]HerdJobPlan, def Resource) HerdJob {
	for _, job := range herdWorkJobs {
		if jobs[job].Target == def {
			return job
		}
	}
	return HerdJobNone
}

func herdShareOf(n int64, share float64, floor int64) int64 {
	return max(floor, int64(math.Floor(float64(n)*share)))
}

func herdBudgetCeiling(in HerdPlanInput) (float64, bool) {
	headroom, bk := in.Budget.Value()
	w, wk := in.Wealth.Value()
	if !bk || !wk || !finite(headroom) || !finite(w.Total) {
		return 0, false
	}
	return herdBudgetShare(headroom, w.Total)
}

// herdObtainable is the races the colony has or can get: owned, tameable and
// not dangerous in the wild census, or offered. An unread wild census, or a
// wild animal with unknown tame facts, offers nothing.
func herdObtainable(in HerdPlanInput, owned map[Resource]*herdRace) map[Resource]bool {
	out := map[Resource]bool{}
	for def := range owned {
		out[def] = true
	}
	for _, def := range in.Offers {
		out[def] = true
	}
	wild, _ := in.Wild.Value()
	for _, a := range wild {
		if tameable, ok := a.Tameable.Value(); ok && tameable && !herdDangerous(a) {
			out[a.Definition] = true
		}
	}
	return out
}

// herdWarWanted is whether the colony wants a war animal it has none of: the
// wealth budget is read and has no negative headroom, so the defense keeps
// pace with the wealth the animals add. Held war animals are kept (and cut by
// the budget ceiling) whatever this says.
func herdWarWanted(in HerdPlanInput) bool {
	headroom, ok := in.Budget.Value()
	return ok && finite(headroom) && headroom >= 0
}

// herdCandidates are the races ranked for the job. War candidates the colony
// does not own also need a handler whose Animals skill clears the race's
// minimum handling skill, read from the catalog (an unread roster or minimum
// leaves the race out). Dangerous wild races join the war candidates only
// when they out-fight every other candidate (herdDangerousWar).
func herdCandidates(job HerdJob, in HerdPlanInput, owned map[Resource]*herdRace, obtainable map[Resource]bool) map[Resource]bool {
	if job != HerdJobWar {
		return obtainable
	}
	profiles, _ := in.Handlers.Value()
	out := map[Resource]bool{}
	for def := range obtainable {
		race, _ := in.Races.Race(def)
		minimum, known := race.MinimumHandlingSkill.Value()
		if _, handled := TamerFor(profiles, minimum); owned[def] != nil || known && handled {
			out[def] = true
		}
	}
	for def := range herdDangerousWar(in, out) {
		out[def] = true
	}
	return out
}

// herdDangerousWar is the tameable dangerous wild races worth the tame risk
// as war animals: those whose catalog CombatPower is above that of the best
// war candidate already owned or obtainable safely, and that a handler can
// tame. With no such candidate any readable dangerous race qualifies. An
// unread CombatPower or minimum handling skill leaves the race out.
func herdDangerousWar(in HerdPlanInput, safe map[Resource]bool) map[Resource]bool {
	best := math.Inf(-1)
	for def := range safe {
		race, _ := in.Races.Race(def)
		if power, _, known := herdPerAdult(HerdJobWar, race); known {
			best = math.Max(best, power)
		}
	}
	profiles, _ := in.Handlers.Value()
	wild, _ := in.Wild.Value()
	out := map[Resource]bool{}
	for _, a := range wild {
		if tameable, ok := a.Tameable.Value(); !ok || !tameable || !herdDangerous(a) || safe[a.Definition] {
			continue
		}
		race, _ := in.Races.Race(a.Definition)
		power, capable, known := herdPerAdult(HerdJobWar, race)
		minimum, mk := race.MinimumHandlingSkill.Value()
		if _, handled := TamerFor(profiles, minimum); capable && known && power > best && mk && handled {
			out[a.Definition] = true
		}
	}
	return out
}

// herdRank orders the obtainable races able to hold the job.
func herdRank(job HerdJob, catalog AnimalRaceCatalog, obtainable map[Resource]bool) []Resource {
	type ranked struct {
		def   Resource
		score float64
		skill int
	}
	var rows []ranked
	for def := range obtainable {
		race, ok := catalog.Race(def)
		if !ok {
			continue
		}
		if _, capable, _ := herdPerAdult(job, race); !capable {
			continue
		}
		score, known := herdScore(job, race)
		if !known {
			continue
		}
		if job == HerdJobWar {
			// A war animal is chosen for the fight it wins, not its feed.
			score, _, _ = herdPerAdult(job, race)
		}
		skill, sk := race.MinimumHandlingSkill.Value()
		if !sk {
			skill = math.MaxInt32
		}
		rows = append(rows, ranked{def, score, skill})
	}
	sort.Slice(rows, func(i, j int) bool {
		a, b := rows[i], rows[j]
		if math.Abs(a.score-b.score) > 1e-9*math.Max(a.score, b.score) {
			return a.score > b.score
		}
		if a.skill != b.skill {
			return a.skill < b.skill
		}
		return a.def < b.def
	})
	out := make([]Resource, len(rows))
	for i, r := range rows {
		out[i] = r.def
	}
	return out
}
