package policy

import (
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The bioferrite harvest rule: a held entity's
// extract-bioferrite flag is set when extracting pays, and the game's own
// Doctor work giver (WorkGiver_ExtractBioferrite) then runs the extraction job
// (JobDriver_ExtractBioferrite). Every rule is the game's, from the decompile:
//
//   - The flag is the order and the work giver offers a job only for a held
//     entity whose flag is set, with no BioferriteExtracted hediff (8 days
//     after an extraction) and with CompProducesBioferrite.BioferritePerDay
//     above zero. That per-day figure is 0 for an entity that produces none and
//     while the hediff stands, so one fact carries both the cooldown and the
//     yield: the flag is not set while an extraction is cooling down, and is
//     left set (the game repeats the job once the hediff ends) afterwards.
//   - An extraction yields floor(perDay * 4) bioferrite (the job's
//     BioferriteModifier), so it pays only when that is at least one.
//   - A powered bioferrite harvester linked to the platform forces the flag
//     false every tick, so setting it there is pointless.
//   - The job fails for an entity in Release mode, which is skipped; studying
//     is unaffected by the flag.
//   - The order is unlocked by the BioferriteExtraction research.
//
// An unread fact is a loud issue, never a guess.

// ResearchBioferriteExtraction is ResearchProjectDefOf.BioferriteExtraction.
const ResearchBioferriteExtraction ResearchProjectID = "BioferriteExtraction"

// bioferriteYieldFactor is JobDriver_ExtractBioferrite.BioferriteModifier: an
// extraction yields floor(BioferritePerDay * 4).
const bioferriteYieldFactor = 4

// BioferriteHarvest is what the colony owes its held entities now: the
// entities whose extract flag is to be set, by pawn ID, and the unread facts
// that kept the rule from deciding.
type BioferriteHarvest struct {
	Enable []domain.PawnID
	Issues []string
}

// BioferriteYield is the bioferrite one extraction yields at perDay.
func BioferriteYield(perDay float64) int {
	return int(math.Floor(perDay * bioferriteYieldFactor))
}

// BioferriteHarvestOwed decides every held, living entity. Research that is
// not finished owes nothing; research that is unread is an issue whenever an
// entity is held.
func BioferriteHarvestOwed(p ContainmentPlanning, research domain.Fact[ResearchFacts]) BioferriteHarvest {
	var out BioferriteHarvest
	entities, ok := p.Entities.Value()
	if !ok {
		return out
	}
	var held []CapturableEntity
	for _, e := range entities {
		dead, dk := e.Dead.Value()
		isHeld, hk := e.Held.Value()
		switch {
		case dk && dead:
		case !hk:
			out.Issues = append(out.Issues, fmt.Sprintf("whether a platform holds entity %s is unread", e.Pawn))
		case isHeld:
			held = append(held, e)
		}
	}
	if len(held) == 0 {
		return out
	}
	facts, ok := research.Value()
	if !ok {
		out.Issues = append(out.Issues, "whether BioferriteExtraction is researched is unread")
		return out
	}
	finished := false
	for _, name := range facts.Finished {
		finished = finished || name == ResearchBioferriteExtraction
	}
	if !finished {
		return out
	}
	for _, e := range held {
		switch enable, reason := bioferriteEnable(e); {
		case reason != "":
			out.Issues = append(out.Issues, reason)
		case enable:
			out.Enable = append(out.Enable, e.Pawn)
		}
	}
	sort.Slice(out.Enable, func(i, j int) bool { return out.Enable[i] < out.Enable[j] })
	return out
}

// bioferriteEnable is whether the held entity's flag is to be set; reason
// names the unread fact otherwise.
func bioferriteEnable(e CapturableEntity) (enable bool, reason string) {
	unread := func(what string) (bool, string) { return false, fmt.Sprintf("%s of entity %s is unread", what, e.Pawn) }
	mode, ok := e.Mode.Value()
	if !ok {
		return unread("the containment mode")
	}
	flag, ok := e.ExtractBioferrite.Value()
	if !ok {
		return unread("the extract bioferrite flag")
	}
	harvester, ok := e.HarvesterAttached.Value()
	if !ok {
		return unread("whether a bioferrite harvester is attached")
	}
	perDay, ok := e.BioferritePerDay.Value()
	if !ok {
		return unread("the bioferrite production")
	}
	if mode == ContainmentRelease || flag || harvester {
		return false, ""
	}
	return BioferriteYield(perDay) >= 1, ""
}

// entityBioferriteOwed is whether a held entity's extract flag is to be set: a
// standing work for MaintainPopulation, whose custody step carries it.
func entityBioferriteOwed(p ContainmentPlanning, research domain.Fact[ResearchFacts]) bool {
	return len(BioferriteHarvestOwed(p, research).Enable) > 0
}
