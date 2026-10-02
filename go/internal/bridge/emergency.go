package bridge

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadEmergency observes basic health and the full status threat census,
// its pawn references joined against the pawn table (#1343). It does not
// bind a controller direction/plan or authorize treatment, combat or building.
func (client *Client) ReadEmergency(ctx context.Context, id *c.Identity) (EmergencyObservation, Result, error) {
	var empty EmergencyObservation
	if err := ValidateIdentity(id); err != nil {
		return empty, Result{}, err
	}
	id = proto.Clone(id).(*c.Identity)
	request := emergencyRequest(id)
	reply := &o.StatusReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_status", request, reply)
	if err != nil {
		return empty, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.StatusReply_Unavailable:
		return empty, raw, unavailable(v.Unavailable, raw)
	case *o.StatusReply_Failure:
		return empty, raw, failure(v.Failure, raw)
	case *o.StatusReply_Observed:
		pawns, err := client.FramePawns(ctx, id)
		if err != nil {
			return empty, raw, err
		}
		result, err := DecodeEmergencyStatus(v.Observed, pawns, id)
		return result, raw, err
	default:
		return empty, raw, contract("emergency status outcome missing")
	}
}

// emergencyRequest is the status read ReadEmergency issues; a bundle's
// emergency section is seeded into the step cache under the same request.
func emergencyRequest(id *c.Identity) *o.StatusRequest {
	return &o.StatusRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(id).(*c.Identity)}, Colonists: proto.Bool(true), Threats: proto.Bool(true)}
}

// DecodeEmergencyStatus shares live boundary validation with captured replay.
// Each pawn reference resolves against pawns, the frame's pawn table; one
// the table lacks leaves that pawn's facts unknown until a later frame.
// The returned facts carry no player authority or controller plan identity.
func DecodeEmergencyStatus(v *o.StatusSnapshot, pawns Pawns, id *c.Identity) (EmergencyObservation, error) {
	if err := ValidateIdentity(id); err != nil {
		return EmergencyObservation{}, err
	}
	if v == nil {
		return EmergencyObservation{}, contract("emergency status missing")
	}
	if err := buildingUnknown(v); err != nil {
		return EmergencyObservation{}, err
	}
	return emergencyStatus(v, pawns, id)
}
func emergencyBool(v *bool) domain.Fact[bool] {
	if v == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*v)
}

func emergencyIssues(issues []*o.ReadIssue, present func(string) bool) error {
	for _, issue := range issues {
		if issue == nil || issue.Field == nil || validID(issue.GetField()) != nil {
			return contract("invalid emergency read issue")
		}
		if err := validateUnavailable(issue.Unavailable); err != nil {
			return err
		}
		if present(issue.GetField()) {
			return contract("emergency field is both present and unavailable")
		}
	}
	return nil
}

// emergencyPawn reads one census reference's facts from its table row,
// returned too; an unresolved reference has only its id known.
func emergencyPawn(ref *c.Ref, pawns Pawns) (policy.EmergencyPawn, *o.PawnState, error) {
	if !uniqueRef(ref, map[string]bool{}) {
		return policy.EmergencyPawn{}, nil, contract("emergency pawn reference malformed")
	}
	row, ok := pawns.Row(ref)
	if !ok {
		return policy.EmergencyPawn{ID: policy.PawnID(ref.GetId())}, nil, nil
	}
	result, err := emergencyFacts(row)
	return result, row, err
}
func emergencyFacts(row *o.PawnState) (policy.EmergencyPawn, error) {
	var result policy.EmergencyPawn
	if err := emergencyIssues(row.Issues, func(field string) bool {
		switch field {
		case "dead":
			return row.Dead != nil
		case "downed":
			return row.Downed != nil
		case "in_bed", "inBed":
			return row.InBed != nil
		case "health":
			return row.Health != nil
		}
		return false
	}); err != nil {
		return result, err
	}
	result = policy.EmergencyPawn{ID: policy.PawnID(row.Pawn.GetId()), Dead: emergencyBool(row.Dead), Downed: emergencyBool(row.Downed), InBed: emergencyBool(row.InBed)}
	mental, err := PawnMentalState(row)
	if err != nil {
		return result, err
	}
	result.MentalState = mental
	if h := row.Health; h != nil {
		if err := emergencyIssues(h.Issues, func(field string) bool {
			switch strings.TrimPrefix(field, "health.") {
			case "bleeding":
				return h.Bleeding != nil
			case "needs_tend", "needsTend":
				return h.NeedsTend != nil
			}
			return false
		}); err != nil {
			return result, err
		}
		result.Bleeding = emergencyBool(h.Bleeding)
		result.NeedsTend = emergencyBool(h.NeedsTend)
	}
	return result, nil
}
func emergencyStatus(v *o.StatusSnapshot, pawns Pawns, id *c.Identity) (EmergencyObservation, error) {
	var result EmergencyObservation
	if v == nil {
		return result, contract("emergency status missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return result, err
	}
	if !sameIdentity(v.Context.Identity, id) {
		return result, contract("emergency identity mismatch")
	}
	// Both sections are requested. The colonist census is complete unless
	// an issue names it, and then it lists no one.
	if v.Threats == nil {
		return result, contract("requested emergency section missing")
	}
	if err := emergencyIssues(v.Issues, func(field string) bool { return field == "colonists" && len(v.Colonists) > 0 || field == "threats" }); err != nil {
		return result, err
	}
	result.Facts.ColonistsComplete = domain.Known(true)
	for _, issue := range v.Issues {
		if issue.GetField() == "colonists" {
			result.Facts.ColonistsComplete = domain.Unknown[bool]()
		}
	}
	statuses := map[policy.PawnID]policy.EmergencyPawn{}
	observe := func(pawn policy.EmergencyPawn) error {
		if prior, ok := statuses[pawn.ID]; ok {
			for _, pair := range [][2]domain.Fact[bool]{{prior.Dead, pawn.Dead}, {prior.Downed, pawn.Downed}} {
				a, ak := pair[0].Value()
				b, bk := pair[1].Value()
				if ak && bk && a != b {
					return contract("conflicting emergency pawn status")
				}
			}
			if _, known := prior.Dead.Value(); known {
				pawn.Dead = prior.Dead
			}
			if _, known := prior.Downed.Value(); known {
				pawn.Downed = prior.Downed
			}
		}
		statuses[pawn.ID] = pawn
		return nil
	}
	seen := map[policy.PawnID]bool{}
	for _, ref := range v.Colonists {
		pawn, _, err := emergencyPawn(ref, pawns)
		if err != nil {
			return EmergencyObservation{}, err
		}
		if seen[pawn.ID] {
			return EmergencyObservation{}, contract("duplicate emergency colonist")
		}
		seen[pawn.ID] = true
		if err = observe(pawn); err != nil {
			return EmergencyObservation{}, err
		}
		result.Facts.Colonists = append(result.Facts.Colonists, pawn)
	}
	// Go classifies the native's threat fact rows (#1356); the threats are
	// listed kind by kind in ThreatKind order, each kind in row order.
	byKind := map[policy.ThreatKind][]policy.EmergencyThreat{}
	seen = map[policy.PawnID]bool{}
	for _, row := range v.Threats.Pawns {
		if row == nil {
			return EmergencyObservation{}, contract("missing threat pawn")
		}
		if row.NearestColonistDistance != nil && !(row.GetNearestColonistDistance() >= 0) || row.MentalState != nil && validID(row.GetMentalState()) != nil || !optionalRef(row.Faction) {
			return EmergencyObservation{}, contract("invalid emergency threat facts")
		}
		kinds := ClassifyThreat(row)
		if len(kinds) == 0 {
			continue
		}
		pawn, state, err := emergencyPawn(row.Pawn, pawns)
		if err != nil {
			return EmergencyObservation{}, err
		}
		if seen[pawn.ID] {
			return EmergencyObservation{}, contract("duplicate emergency threat")
		}
		seen[pawn.ID] = true
		if err = observe(pawn); err != nil {
			return EmergencyObservation{}, err
		}
		for _, kind := range kinds {
			threat := policy.EmergencyThreat{ID: pawn.ID, Kind: kind, Dead: pawn.Dead, Downed: pawn.Downed}
			if kind == policy.Hostile {
				threat.Passive = emergencyBool(row.Passive)
			}
			if row.NearestColonistDistance != nil {
				threat.Distance = domain.Known(row.GetNearestColonistDistance())
			}
			if state != nil {
				threat.Animal, threat.Fogged = emergencyBool(state.Animal), emergencyBool(state.Fogged)
				if at := state.GetPawn().GetPosition(); at != nil && at.X != nil && at.Z != nil {
					threat.Position = domain.Known(domain.Cell{X: at.GetX(), Z: at.GetZ()})
				}
			}
			byKind[kind] = append(byKind[kind], threat)
		}
	}
	for _, kind := range []policy.ThreatKind{policy.Hostile, policy.HuntingPredator, policy.IgnoredHunter, policy.NearbyPredator, policy.NearbyDowned} {
		result.Facts.Threats = append(result.Facts.Threats, byKind[kind]...)
	}
	seen = map[policy.PawnID]bool{}
	for _, row := range v.Threats.HostileBuildings {
		threat, err := emergencyBuilding(row, v.Context)
		if err != nil {
			return EmergencyObservation{}, err
		}
		if seen[threat.ID] || statuses[threat.ID].ID != "" {
			return EmergencyObservation{}, contract("duplicate emergency hostile building")
		}
		seen[threat.ID] = true
		result.Facts.Threats = append(result.Facts.Threats, threat)
	}
	result.Context = proto.Clone(v.Context).(*c.ObservationContext)
	return result, nil
}

// emergencyBuilding reads one hostile-building row: a spawned building the
// census lists is standing (not dead) and never downed; its CAS token must
// be the row's own, scoped to this context, since the attack order sends it
// back as the target precondition.
func emergencyBuilding(row *o.ThreatBuilding, ctx *c.ObservationContext) (policy.EmergencyThreat, error) {
	var result policy.EmergencyThreat
	if row == nil || row.Building == nil {
		return result, contract("missing threat building")
	}
	if err := pawnsEntity(row.Building, ctx); err != nil {
		return result, err
	}
	if row.BuildingSnapshot == nil || row.Building.DefName == nil {
		return result, contract("threat building snapshot or definition missing")
	}
	if row.HitPoints != nil && row.GetHitPoints() < 0 || row.MaxHitPoints != nil && row.GetMaxHitPoints() < 0 || row.NearestColonistDistance != nil && row.GetNearestColonistDistance() < 0 {
		return result, contract("invalid threat building facts")
	}
	// The occupied rect is the ranged target: every cell in bounds, none
	// twice, at least one (a spawned building occupies its position).
	cells := RectCells(row.Occupied)
	if len(cells) == 0 || cells[0].X < 0 || cells[0].Z < 0 {
		return result, contract("invalid threat building occupied rect")
	}
	result = policy.EmergencyThreat{ID: policy.PawnID(row.Building.GetId()), Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false),
		SnapshotToken: row.BuildingSnapshot.GetToken(), Definition: row.Building.GetDefName(), Cells: cells, Passive: emergencyBool(row.Passive), Mortar: row.GetMortar()}
	if row.NearestColonistDistance != nil {
		result.Distance = domain.Known(float64(row.GetNearestColonistDistance()))
	}
	return result, nil
}
