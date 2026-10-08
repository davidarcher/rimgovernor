package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

type QuestMonument struct {
	Marker, Def                           string
	Map                                   domain.MapID
	Packed, Installed, Complete, AllDone  domain.Fact[bool]
	Cell                                  domain.Cell
	DisallowedBuilding                    string
	DisallowedTicks                       domain.Fact[int64]
	InstallCells                          []domain.Cell
	Pieces                                []QuestMonumentPiece
	Resources                             []QuestMonumentResource
	Offered, ClearSite                    domain.Fact[bool]
	SuppliedResources, AvailableResources []Amount
}

type QuestMonumentPiece struct {
	Def, Stuff             string
	Offset                 domain.Cell
	Rotation               domain.Rotation
	Built, Queued, Allowed domain.Fact[bool]
	AllowedStuffs          []string
	Footprint              []domain.Cell
	BuildOptions           []QuestMonumentBuildOption
}
type QuestMonumentBuildOption struct {
	Stuff string
	Costs []Amount
	Work  float64
}

type QuestMonumentResource struct {
	ID, Def   string
	Cell      domain.Cell
	InStorage domain.Fact[bool]
	Haulers   []domain.PawnID
}

type QuestMonumentMethod struct {
	Quest  domain.QuestID
	Kind   string
	Move   domain.MoveBuilding
	Build  domain.Building
	Haul   domain.Haul
	Reason string
}

func MonumentDeficit(f RoundsFacts) bool {
	rows, known := f.QuestOffers.Value()
	if !known {
		return false
	}
	for _, offer := range rows {
		if offer.State != "Ongoing" {
			continue
		}
		for _, objective := range offer.Objectives {
			if _, known := objective.Monument.Value(); known {
				return true
			}
		}
	}
	return false
}

// SelectQuestMonument follows native sketch progress. Queued construction and
// an intact completed marker remain native work; receipts never complete it.
func SelectQuestMonument(offers domain.Fact[[]JoinerOffer], home domain.MapID, stock map[Resource]int64, protected []Rectangle) QuestMonumentMethod {
	rows, known := offers.Value()
	if !known {
		return QuestMonumentMethod{Reason: "quest_census_unknown"}
	}
	rows = append([]JoinerOffer(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Quest < rows[j].Quest })
	for _, offer := range rows {
		if offer.State != "Ongoing" {
			continue
		}
		for _, objective := range offer.Objectives {
			marker, known := objective.Monument.Value()
			if !known || marker.Map != home {
				continue
			}
			step := QuestMonumentMethod{Quest: offer.Quest}
			if marker.DisallowedBuilding != "" {
				step.Reason = "disallowed_building"
				return step
			}
			if done, known := marker.AllDone.Value(); known && done {
				step.Kind = "protect"
				return step
			}
			if packed, known := marker.Packed.Value(); known && packed {
				for _, cell := range marker.InstallCells {
					blocked := false
					for _, rect := range protected {
						for _, piece := range marker.Pieces {
							for _, occupied := range piece.Footprint {
								if rectContains(rect, domain.Cell{X: cell.X + occupied.X, Z: cell.Z + occupied.Z}) {
									blocked = true
								}
							}
						}
					}
					if blocked {
						continue
					}
					move, err := domain.NewMoveBuilding(marker.Marker, marker.Def, cell, domain.North)
					if err == nil {
						step.Kind = "install"
						step.Move = move
						return step
					}
				}
				step.Reason = "install_site_unavailable"
				return step
			}
			installed, known := marker.Installed.Value()
			if !known || !installed {
				step.Reason = "marker_unavailable"
				return step
			}
			for _, resource := range marker.Resources {
				stored, known := resource.InStorage.Value()
				if !known || stored || len(resource.Haulers) == 0 {
					continue
				}
				haulers := append([]domain.PawnID(nil), resource.Haulers...)
				sort.Slice(haulers, func(i, j int) bool { return haulers[i] < haulers[j] })
				haul, err := domain.NewHaul(haulers[0], resource.ID, resource.Def, resource.Cell)
				if err == nil {
					step.Kind = "haul"
					step.Haul = haul
					return step
				}
			}
			for _, piece := range marker.Pieces {
				built, bk := piece.Built.Value()
				queued, qk := piece.Queued.Value()
				allowed, ak := piece.Allowed.Value()
				if !bk || !qk || !ak {
					step.Reason = "sketch_unknown"
					return step
				}
				if built || queued {
					continue
				}
				if !allowed {
					step.Reason = "sketch_placement_refused"
					return step
				}
				stuff := piece.Stuff
				if stuff == "" && len(piece.AllowedStuffs) > 0 {
					choices := append([]string(nil), piece.AllowedStuffs...)
					sort.Strings(choices)
					for _, candidate := range choices {
						if stock[Resource(candidate)] > 0 && (stuff == "" || stock[Resource(candidate)] > stock[Resource(stuff)]) {
							stuff = candidate
						}
					}
					if stuff == "" {
						step.Reason = "monument_materials"
						return step
					}
				}
				build, err := domain.NewBuilding(piece.Def, domain.Cell{X: marker.Cell.X + piece.Offset.X, Z: marker.Cell.Z + piece.Offset.Z}, piece.Rotation, stuff)
				if err != nil {
					step.Reason = "sketch_invalid"
					return step
				}
				step.Kind = "build"
				step.Build = build
				return step
			}
			step.Kind = "wait"
			return step
		}
	}
	return QuestMonumentMethod{}
}
