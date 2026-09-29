package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GearMaterialBudget is the loadout model's Budget: what each measured
// material can fund after holds, the same
// floor food bills honour (#470). Unmeasured resources are absent, which the
// model treats as unfunded.
func GearMaterialBudget(stock []Stock, holds []Amount) []Amount {
	available, known := gearAvailable(stock, holds)
	out := []Amount{}
	for resource := range known {
		out = append(out, Amount{resource, available[resource]})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Resource < out[j].Resource })
	return out
}

// TierStyleStockOf folds a stock census into the map the rules read,
// summing repeated rows and ignoring negative counts.
func TierStyleStockOf(rows []Amount) TierStyleStock {
	stock := TierStyleStock{}
	for _, row := range rows {
		if row.Count > 0 {
			stock[row.Resource] += row.Count
		}
	}
	return stock
}

// WasteDeficit is the binary MaintainWaste
// deficit signal: unknown census stays unknown (absence is never evidence of
// recovery), otherwise deficit is simply "any pending item remains".
func WasteDeficit(items domain.Fact[[]WasteItem]) domain.Fact[bool] {
	rows, known := items.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(pendingWaste(rows)) > 0)
}

// Tier returns the named tier; ok is false for an unknown name.
func (l DefenseLayout) Tier(name DefenseTierName) (DefenseTier, bool) {
	for _, t := range l.Tiers {
		if t.Name == name {
			return t, true
		}
	}
	return DefenseTier{}, false
}

// HorseshoesLane is the canonical rectangle a pin at the back wall keeps
// clear: three cells wide, from the pin five cells toward the entrance.
func HorseshoesLane(f InteriorFrame) Rectangle {
	return Rectangle{X: CentreStart(f.Width, 1) - 1, Z: f.Depth - horseshoesLane, Width: 3, Height: horseshoesLane - 1}
}

// TurbineWindCells mirrors WindTurbineUtility.CalculateWindCells for a
// 7x2 turbine: 7 wide, 10 rows in front and 6 behind.
func TurbineWindCells(center domain.Cell, rot domain.Rotation) []domain.Cell {
	off, front, back := int32(0), int32(9), int32(5)
	if rot != domain.North && rot != domain.East {
		off, front, back = -1, 5, 9
	}
	var a, b Rectangle // X, Z, Width, Height as min/extent
	if rot == domain.East || rot == domain.West {
		a = Rectangle{X: center.X + 2 + off, Z: center.Z - 3, Width: front + 1, Height: 7}
		b = Rectangle{X: center.X - 1 - back + off, Z: center.Z - 3, Width: back + 1, Height: 7}
	} else {
		a = Rectangle{X: center.X - 3, Z: center.Z + 2 + off, Width: 7, Height: front + 1}
		b = Rectangle{X: center.X - 3, Z: center.Z - 1 - back + off, Width: 7, Height: back + 1}
	}
	var out []domain.Cell
	for _, r := range []Rectangle{a, b} {
		for z := r.Z; z < r.Z+r.Height; z++ {
			for x := r.X; x < r.X+r.Width; x++ {
				out = append(out, domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}

// MoodUnownedThought reports whether the thought is removable environment
// pressure no goal owns.
func MoodUnownedThought(def string) bool { return moodUnownedThoughts[def] }

// MoodProvisionOwners names the goals whose facility removes the thought,
// if the catalog knows any.
func MoodProvisionOwners(def string) []GoalID {
	return append([]GoalID(nil), moodProvisionOwners[def]...)
}

// KnownTraits lists the trait rows the table knows, for documentation and
// the dossier.
func KnownTraits() []PawnTrait {
	out := make([]PawnTrait, 0, len(traitTable))
	for t := range traitTable {
		out = append(out, t)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Name != out[j].Name {
			return out[i].Name < out[j].Name
		}
		return out[i].Degree < out[j].Degree
	})
	return out
}

// WoodProposals: each designatable tree is one cut.
func WoodProposals(goal GoalID, method domain.MethodID, definition string, trees []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range trees {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "cut_plant:" + definition, Work: WorkPlantCutting, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// SupplyHaulProposals: each loose stack is one haul; two goals naming the
// same stack project one candidate.
func SupplyHaulProposals(goal GoalID, method domain.MethodID, definition string, things []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range things {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "haul:" + definition, Work: WorkHauling, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// AnimalFeedProposals: MaintainAnimalFeed's methods as alternatives of one
// group. A stock-sourced method is a haul; kibble is a bill on any of the
// shared benches (each bench an alternative) that can run only when its
// ingredients are observed; a hay field is growing on its cells.
func AnimalFeedProposals(goal GoalID, m AnimalFeedMethod, ingredients domain.Fact[bool], hay []domain.Cell) []ReadyProposal {
	group := string(goal) + "/feed"
	var out []ReadyProposal
	if m.Resource == AnimalFeedFallbackResource {
		for _, b := range m.Benches {
			out = append(out, ReadyProposal{Goal: goal, Method: "kibble", Stage: "bill:Make_Kibble", Work: WorkCooking, Claims: []ReadyClaim{{"bench", b}}, Alternative: group, Eligible: ingredients, Parallelism: 1})
		}
	} else if m.Resource != "" {
		out = append(out, ReadyProposal{Goal: goal, Method: domain.MethodID("stock-" + string(m.Resource)), Stage: "haul:" + string(m.Resource), Work: WorkHauling, Alternative: group, Eligible: domain.Known(m.Delivered), Reason: reasonIf(!m.Delivered, string(domain.HeldStorageMissing)), Parallelism: 1})
	}
	if len(hay) > 0 {
		var claims []ReadyClaim
		for _, c := range hay {
			claims = append(claims, CellClaim(c))
		}
		out = append(out, ReadyProposal{Goal: goal, Method: "hay", Stage: "grow:Hay", Work: WorkGrowing, Claims: claims, Alternative: group, Eligible: domain.Known(true), Parallelism: 1})
	}
	return out
}

func reasonIf(cond bool, reason string) string {
	if cond {
		return reason
	}
	return ""
}

// MirrorPiece is a piece's mirror image across the frame's centre line,
// under a new slot name.
func (f InteriorFrame) MirrorPiece(p InteriorPiece, slot string) InteriorPiece {
	p.Slot = slot
	p.Rect.X = MirrorStart(f.Width, p.Rect.X, p.Rect.Width)
	if p.Rot == domain.East || p.Rot == domain.West {
		p.Rot = rotateCW(p.Rot, 2)
	}
	return p
}

// MirrorStart is where the mirror image of a span starting at start lies.
func MirrorStart(length, start, span int32) int32 { return length - start - span }

// Batteries is the number of batteries that close the storage shortfall.
func (b PowerBudget) Batteries() int {
	if b.StorageShortfallWD <= 0 {
		return 0
	}
	return int(math.Ceil(b.StorageShortfallWD / BatteryCapacityWD))
}
