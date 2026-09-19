package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// shrineReadinessNative is what judging a breach needs beyond the shrine
// census (#457): the colonists' combat facts, the built traps around the
// breach wall and the emergency census. A native that lacks any of them
// leaves every shrine's readiness unknown.
type shrineReadinessNative interface {
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
}

// shrineTrapWindow is the half-width of the defense-site window read around
// a breach wall's outside cell: one policy trap radius plus a cell, well
// under the read's 2048-cell bound.
const shrineTrapWindow = 13

// ShrineReadinessReport is one shrine's breach judgement with the reading
// it was made from.
type ShrineReadinessReport struct {
	Shrine    string
	Readiness policy.ShrineReadiness
	// Standing are the unfogged, walkable, unbuilt cells of the trap window
	// read around the chosen wall (the breach squad's candidate positions,
	// #458) and Traps the built spike traps there; empty for a shrine the
	// window was not read for.
	Standing, Traps []domain.Cell
}

// emergencyActive reports whether the emergency census lists a threat that
// still stands; dormant shrine guards are not listed until they wake.
func emergencyActive(facts policy.EmergencyFacts) bool {
	for _, threat := range facts.Threats {
		dead, dk := threat.Dead.Value()
		downed, wk := threat.Downed.Value()
		if !(dk && dead) && !(wk && downed) {
			return true
		}
	}
	return false
}

// shrineReadiness judges every sealed shrine that shows a breach wall. The
// costly reads happen once and only when such a shrine exists; shrines
// with nothing to breach are judged from the census alone. raidPoints is
// the colony census's reading; the storyteller is not observed, so the
// Peaceful softening stays off until it is. A nil colonists list takes the
// emergency census's colonists.
func shrineReadiness(ctx context.Context, native shrineReadinessNative, identity *c.Identity, shrines []policy.AncientShrine, colonists []string, raidPoints domain.Fact[float64], center domain.Cell, bounds policy.Bounds) ([]ShrineReadinessReport, error) {
	out := make([]ShrineReadinessReport, 0, len(shrines))
	needsReads := false
	for _, shrine := range shrines {
		if shrine.Sealed && len(shrine.BreachWalls) > 0 {
			needsReads = true
		}
	}
	var squad []policy.ShrineDefenderFacts
	emergency := false
	if needsReads {
		observed, _, err := native.ReadEmergency(ctx, identity)
		if err != nil {
			return nil, err
		}
		emergency = emergencyActive(observed.Facts)
		if colonists == nil {
			for _, pawn := range observed.Facts.Colonists {
				colonists = append(colonists, string(pawn.ID))
			}
		}
		pawns, _, err := native.ReadCombatPawns(ctx, identity, colonists)
		if err != nil {
			return nil, err
		}
		for _, row := range pawns.GetObserved().GetPawns() {
			if row == nil || row.Pawn == nil {
				continue
			}
			facts := policy.ShrineDefenderFacts{SquadDefenderFacts: squadDefenderFacts(row)}
			if reach := primaryRange(row.Equipment); reach > 0 {
				facts.WeaponRange = domain.Known(reach)
			}
			squad = append(squad, facts)
		}
	}
	for _, shrine := range shrines {
		request := policy.ShrineReadinessRequest{Shrine: shrine, RaidPoints: raidPoints, Peaceful: domain.Unknown[bool](), Squad: squad, Center: center, Emergency: emergency}
		report := ShrineReadinessReport{Shrine: shrine.ID}
		if shrine.Sealed && len(shrine.BreachWalls) > 0 {
			// The wall the policy will choose is the one nearest the centre;
			// judge it first so the trap window is read around it.
			wall := policy.ShrineBreachReadiness(policy.ShrineReadinessRequest{Shrine: shrine, Center: center}).Wall
			region := bridge.CellRect{Min: domain.Cell{X: wall.Outside.X - shrineTrapWindow, Z: wall.Outside.Z - shrineTrapWindow}, Max: domain.Cell{X: wall.Outside.X + shrineTrapWindow, Z: wall.Outside.Z + shrineTrapWindow}}
			if region.Min.X < 0 {
				region.Min.X = 0
			}
			if region.Min.Z < 0 {
				region.Min.Z = 0
			}
			if bounds.Width > 0 && region.Max.X > bounds.Width-1 {
				region.Max.X = bounds.Width - 1
			}
			if bounds.Height > 0 && region.Max.Z > bounds.Height-1 {
				region.Max.Z = bounds.Height - 1
			}
			site, _, err := native.ReadDefenseSite(ctx, identity, region)
			if err != nil {
				return nil, err
			}
			for _, cell := range site.Cells {
				switch {
				case cell.Fogged:
				case cell.PlayerOwned && cell.EdificeDefName == defenseDefinitions.Trap:
					request.Traps = append(request.Traps, cell.Cell)
				case cell.Walkable && cell.EdificeDefName == "":
					report.Standing = append(report.Standing, cell.Cell)
				}
			}
			report.Traps = request.Traps
		}
		report.Readiness = policy.ShrineBreachReadiness(request)
		out = append(out, report)
	}
	return out, ctx.Err()
}

// routineShrineHolds judges every shrine the review's census lists for the
// journal (#458): the readiness reason, guards_alive after a breach, or
// ready with the chosen wall. A native without the readiness reads leaves
// every row readiness_unknown; an unknown census leaves no rows.
func routineShrineHolds(ctx context.Context, native any, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection) ([]policy.ShrineHold, error) {
	shrines, known := projection.Facts.Upkeep.Shrines.Value()
	if !known || len(shrines) == 0 {
		return nil, nil
	}
	reads, ok := native.(shrineReadinessNative)
	if !ok {
		out := make([]policy.ShrineHold, 0, len(shrines))
		for _, shrine := range shrines {
			out = append(out, policy.ShrineHold{Shrine: shrine.ID, Reason: ShrineHoldReadinessUnknown})
		}
		return out, nil
	}
	reports, err := shrineReadiness(ctx, reads, boundary.Identity(snapshot), shrines, nil, projection.Threat.RaidPoints, projection.Center, projection.Bounds)
	if err != nil {
		return nil, err
	}
	out := make([]policy.ShrineHold, 0, len(reports))
	for i, report := range reports {
		out = append(out, policy.ShrineHold{Shrine: shrines[i].ID, Reason: policy.ShrineHoldReason(shrines[i], report.Readiness), Wall: report.Readiness.Wall.EntityID})
	}
	return out, nil
}

// ShrineHoldReadinessUnknown is the journal's reason under a native that
// cannot read combat pawns, the defense site or the emergency census.
const ShrineHoldReadinessUnknown = "readiness_unknown"
