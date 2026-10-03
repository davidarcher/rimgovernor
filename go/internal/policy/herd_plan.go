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

// herdMilkFirst is the tie-break for equally scored milk races.
const herdMilkFirst Resource = "Cow"

// HerdPlanInput is every fact the herd plan derives from. Unknown facts leave
// the plan unchanged: an unread catalog plans no job, and a race whose yield,
// size or capacity is unread is neither ranked nor retired.
type HerdPlanInput struct {
	Animals, Wild domain.Fact[[]UpkeepAnimal]
	Races         *AnimalRaceCatalog
	Budget        domain.Fact[float64]
	Wealth        domain.Fact[WealthFacts]
	Pens          domain.Fact[[]PenGrazing]
	Food          domain.Fact[FoodPlan]
	// Offers are races a trader offers or a quest rewards now; they count
	// as obtainable (the buying and reward hooks, #1636).
	Offers []Resource
}

// HerdPlanInput is the plan's facts from a routine reading.
func (f RoutineFacts) HerdPlanInput() HerdPlanInput {
	return HerdPlanInput{Animals: f.AnimalUpkeep.Animals, Wild: f.AnimalUpkeep.WildAnimals, Races: f.AnimalUpkeep.AnimalRaces,
		Budget: f.WealthBudget(), Wealth: f.Wealth, Pens: f.PenGrazing, Food: f.FoodPlan}
}

// HerdPolicy is the population band of the herd plan of f.
func (f RoutineFacts) HerdPolicy() HerdPolicy { return PlanHerd(f.HerdPlanInput()).Policy }

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
}

type herdRace struct {
	race                       AnimalRace
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
	per, capable, known := herdPerAdult(job, race)
	if !capable {
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
	if holds && known && !herdJuvenile(a) {
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
		for def, floor := range floors {
			plan.Policy.PopulationMin[def] = floor
		}
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
			if race, ok := in.Races.Race(a.Definition); ok {
				s.race = race
			}
		}
		s.n++
		switch a.Gender {
		case "Male":
			s.males++
		case "Female":
			s.females++
		case "None":
			s.asexual++
		default:
			s.unsexed = true
		}
		if bonded, _ := a.Bonded.Value(); bonded {
			s.bonded = true
		}
		if s.race.Def == "" {
			continue
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
	targets := map[Resource]int64{}
	covered := map[Resource]bool{}
	for _, job := range herdWorkJobs {
		var holders []Resource
		for _, def := range order {
			if stats[def].holds[job] {
				holders = append(holders, def)
			}
		}
		if len(holders) == 0 || in.Races == nil {
			continue
		}
		frozen := false
		for _, def := range holders {
			if _, ok := herdScore(job, stats[def].race); !ok {
				frozen = true
			}
		}
		jp := HerdJobPlan{Job: job}
		if !frozen {
			jp.Ranked = herdRank(job, in.Races, obtainable)
		}
		if len(jp.Ranked) == 0 {
			for _, def := range holders {
				herdKeep(keeps, def, job)
			}
			plan.Jobs[job] = jp
			continue
		}
		jp.Target = jp.Ranked[0]
		for _, def := range holders {
			covered[def] = true
		}
		preferred[jp.Target] = true
		herdKeep(keeps, jp.Target, job)
		target := stats[jp.Target]
		if target == nil {
			target = &herdRace{capacity: map[HerdJob]float64{}}
		}
		targetRace, _ := in.Races.Race(jp.Target)
		perTarget, _, perKnown := herdPerAdult(job, targetRace)
		demand := target.capacity[job]
		others, foodDemand := 0.0, 0.0
		for _, def := range holders {
			s := stats[def]
			if per, _, known := herdPerAdult(job, s.race); known {
				foodDemand += float64(floors[def]) * per
			}
			if def == jp.Target {
				continue
			}
			others += s.capacity[job]
			if target.paired() && target.capacity[job] > 0 && target.capacity[job] >= s.capacity[job] {
				continue
			}
			herdKeep(keeps, def, job)
		}
		demand = math.Max(demand, math.Max(others, foodDemand))
		jp.Heads = herdPairSize
		if perKnown && !math.IsInf(demand, 0) {
			jp.Heads = max(jp.Heads, int64(math.Ceil(demand/perTarget)))
		}
		targets[jp.Target] = max(targets[jp.Target], jp.Heads)
		plan.Jobs[job] = jp
	}
	for _, def := range order {
		s := stats[def]
		role := HerdRole{Race: def, Job: HerdJobNone, Preferred: preferred[def]}
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
	// A race the plan cannot place (no catalog, an unread yield) keeps the
	// food plan's floor, as before the plan existed.
	for def, floor := range floors {
		if _, planned := targets[def]; planned || covered[def] {
			continue
		}
		if ceiling, ok := plan.Policy.PopulationMax[def]; ok {
			floor = min(floor, ceiling)
		}
		plan.Policy.PopulationMin[def] = max(plan.Policy.PopulationMin[def], floor)
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

// herdRank orders the obtainable races able to hold the job.
func herdRank(job HerdJob, catalog *AnimalRaceCatalog, obtainable map[Resource]bool) []Resource {
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
		if job == HerdJobMilk && (a.def == herdMilkFirst) != (b.def == herdMilkFirst) {
			return a.def == herdMilkFirst
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
