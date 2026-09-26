package bridge

import (
	"context"
	"math"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadEmergency observes basic health and the full status threat census. It does
// not bind a controller direction/plan or authorize treatment, combat or building.
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
		result, err := DecodeEmergencyStatus(v.Observed, id)
		return result, raw, err
	default:
		return empty, raw, contract("emergency status outcome missing")
	}
}

// emergencyRequest is the status read ReadEmergency issues; a bundle's
// emergency section is seeded into the step cache under the same request.
func emergencyRequest(id *c.Identity) *o.StatusRequest {
	return &o.StatusRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(id).(*c.Identity)}, Colonists: proto.Bool(true), Threats: proto.Bool(true), ColonistDetail: proto.Bool(false), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
}

// DecodeEmergencyStatus shares live boundary validation with captured replay.
// The returned facts carry no player authority or controller plan identity.
func DecodeEmergencyStatus(v *o.StatusSnapshot, id *c.Identity) (EmergencyObservation, error) {
	if err := ValidateIdentity(id); err != nil {
		return EmergencyObservation{}, err
	}
	if v == nil {
		return EmergencyObservation{}, contract("emergency status missing")
	}
	if err := buildingUnknown(v); err != nil {
		return EmergencyObservation{}, err
	}
	return emergencyStatus(v, id)
}
func emergencyBool(v *bool) domain.Fact[bool] {
	if v == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*v)
}
func emergencyCompleteness(v *o.Completeness, rows int) (domain.Fact[bool], error) {
	unknown := domain.Unknown[bool]()
	if rows > 256 {
		return unknown, contract("emergency census exceeds limit")
	}
	if v == nil {
		return unknown, nil
	}
	if v.Returned != nil && v.GetReturned() != uint64(rows) {
		return unknown, contract("emergency returned count mismatch")
	}
	if v.Matched != nil && v.GetMatched() < uint64(rows) {
		return unknown, contract("emergency matched count mismatch")
	}
	if !diagnostic(v.SnapshotToken) {
		return unknown, contract("invalid snapshot token")
	}
	if v.Page != nil && !diagnostic(v.Page.NextCursor) {
		return unknown, contract("invalid page cursor")
	}
	countsKnown := v.Matched != nil && v.Returned != nil && v.Filtered != nil && v.Unreadable != nil
	if countsKnown {
		if v.GetFiltered() > math.MaxUint64-v.GetReturned() || v.GetUnreadable() > math.MaxUint64-v.GetReturned()-v.GetFiltered() {
			return unknown, contract("emergency count overflow")
		}
		accounted := v.GetReturned() + v.GetFiltered() + v.GetUnreadable()
		if v.GetMatched() < accounted {
			return unknown, contract("inconsistent emergency census counts")
		}
		if v.Page != nil && v.Page.GetComplete() && v.GetMatched() != accounted {
			return unknown, contract("complete emergency census count mismatch")
		}
	}
	if v.Page == nil || v.Page.Complete == nil {
		return unknown, nil
	}
	if v.Page.GetComplete() && v.Page.GetNextCursor() != "" {
		return unknown, contract("complete emergency census has next cursor")
	}
	if !v.Page.GetComplete() {
		return domain.Known(false), nil
	}
	if !countsKnown {
		return unknown, nil
	}
	return domain.Known(v.GetFiltered() == 0 && v.GetUnreadable() == 0), nil
}
func emergencyIssues(issues []*o.ReadIssue, present func(string) bool) error {
	if len(issues) > 256 {
		return contract("too many emergency read issues")
	}
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
func emergencyPawn(row *o.PawnState) (policy.EmergencyPawn, error) {
	var result policy.EmergencyPawn
	if row == nil || row.Pawn == nil || row.Pawn.Id == nil || validID(row.Pawn.GetId()) != nil {
		return result, contract("emergency pawn identity missing")
	}
	if row.Pawn.MapId != nil && row.Pawn.GetMapId() < 0 {
		return result, contract("negative emergency pawn map")
	}
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
func emergencyStatus(v *o.StatusSnapshot, id *c.Identity) (EmergencyObservation, error) {
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
	if v.Colonists == nil || v.Threats == nil {
		return result, contract("requested emergency section missing")
	}
	if err := emergencyIssues(v.Issues, func(field string) bool { return field == "colonists" || field == "threats" }); err != nil {
		return result, err
	}
	if err := ValidateContext(v.Colonists.Context); err != nil {
		return result, err
	}
	if !proto.Equal(v.Colonists.Context, v.Context) {
		return result, contract("colonist context mismatch")
	}
	var err error
	result.Facts.ColonistsComplete, err = emergencyCompleteness(v.Colonists.Completeness, len(v.Colonists.Pawns))
	if err != nil {
		return EmergencyObservation{}, err
	}
	type category struct {
		kind policy.ThreatKind
		rows []*o.ThreatPawn
	}
	categories := []category{{policy.Hostile, v.Threats.Hostiles}, {policy.HuntingPredator, v.Threats.HuntingPredators}, {policy.IgnoredHunter, v.Threats.IgnoredHunters}, {policy.NearbyPredator, v.Threats.WildPredatorsNear}, {policy.NearbyDowned, v.Threats.DownedNear}}
	count := len(v.Threats.HostileBuildings)
	for _, group := range categories {
		count += len(group.rows)
	}
	result.Facts.ThreatsComplete, err = emergencyCompleteness(v.Threats.Completeness, count)
	if err != nil {
		return EmergencyObservation{}, err
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
	for _, row := range v.Colonists.Pawns {
		pawn, err := emergencyPawn(row)
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
	for _, group := range categories {
		seen = map[policy.PawnID]bool{}
		for _, row := range group.rows {
			if row == nil {
				return EmergencyObservation{}, contract("missing threat pawn")
			}
			pawn, err := emergencyPawn(row.Pawn)
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
			threat := policy.EmergencyThreat{ID: pawn.ID, Kind: group.kind, Dead: pawn.Dead, Downed: pawn.Downed, Animal: emergencyBool(row.Pawn.Animal), Fogged: emergencyBool(row.Pawn.Fogged)}
			if row.Pawn.NearestColonistDistance != nil {
				threat.Distance = domain.Known(row.Pawn.GetNearestColonistDistance())
			}
			result.Facts.Threats = append(result.Facts.Threats, threat)
		}
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
	if row.Building.Snapshot == nil || row.Building.DefName == nil {
		return result, contract("threat building snapshot or definition missing")
	}
	if row.HitPoints != nil && row.GetHitPoints() < 0 || row.MaxHitPoints != nil && row.GetMaxHitPoints() < 0 || row.NearestColonistDistance != nil && row.GetNearestColonistDistance() < 0 {
		return result, contract("invalid threat building facts")
	}
	// The occupied rect is the ranged target: every cell in bounds, none
	// twice, at least one (a spawned building occupies its position).
	if len(row.OccupiedCells) == 0 || len(row.OccupiedCells) > 1024 {
		return result, contract("threat building occupied cells missing")
	}
	cells := make([]domain.Cell, 0, len(row.OccupiedCells))
	seen := map[domain.Cell]bool{}
	for _, cell := range row.OccupiedCells {
		if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
			return result, contract("invalid threat building occupied cell")
		}
		at := domain.Cell{X: cell.GetX(), Z: cell.GetZ()}
		if seen[at] {
			return result, contract("duplicate threat building occupied cell")
		}
		seen[at] = true
		cells = append(cells, at)
	}
	result = policy.EmergencyThreat{ID: policy.PawnID(row.Building.GetId()), Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false),
		SnapshotToken: row.Building.Snapshot.GetToken(), Definition: row.Building.GetDefName(), Cells: cells}
	if row.NearestColonistDistance != nil {
		result.Distance = domain.Known(float64(row.GetNearestColonistDistance()))
	}
	return result, nil
}
