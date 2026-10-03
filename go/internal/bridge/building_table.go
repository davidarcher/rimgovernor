package bridge

import (
	"context"
	"math"
	"sync"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Buildings is a frame's building table by id (#1343): the canonical rows
// every other section's building reference resolves against. A reference
// the table does not hold waits for the next frame: the fact it feeds is
// unknown until then.
type Buildings struct{ Table[*o.BuildingState] }

// NewBuildings is the table of rows by their building id; a row with no
// id is not addressable and is left out.
func NewBuildings(rows ...*o.BuildingState) Buildings {
	var t Table[*o.BuildingState]
	for _, row := range rows {
		if id := row.GetBuilding().GetId(); id != "" {
			t = t.Set(id, row)
		}
	}
	return Buildings{t}
}

// BuildingCensusOf is the building census of a whole building snapshot:
// its rows by id and the first invalid built row.
func BuildingCensusOf(v *o.BuildingsSnapshot) *BuildingCensus {
	if v == nil {
		return nil
	}
	out := &BuildingCensus{Context: v.Context, Rows: BuildingTable(v)}
	for _, row := range v.Buildings {
		if out.Invalid = checkBuiltRow(row, v.Context); out.Invalid != nil {
			break
		}
	}
	return out
}

// With is the table with row added or replaced under id.
func (b Buildings) With(id string, row *o.BuildingState) Buildings { return Buildings{b.Set(id, row)} }

// Without is the table without id.
func (b Buildings) Without(id string) Buildings { return Buildings{b.Delete(id)} }

// BuildingCensus is a frame's player building table (the routine frame's
// construction census): the held table version, the frame's context and
// the first invalid built row, which fails any reader of the construction
// census.
type BuildingCensus struct {
	Context *c.ObservationContext
	Rows    Buildings
	Invalid error
	// Memo is the stream's memo for what a reader derives from the table
	// between frames (observation's incremental construction census); nil
	// for a census decoded from a recording.
	Memo *Memo
}

// Memo holds one derived value a reader updates frame to frame: update
// gets the last value (nil at first) and returns the next. Calls are
// serialised.
type Memo struct {
	mu sync.Mutex
	v  any
}

// Update replaces the memo's value with update(last) and returns it.
func (m *Memo) Update(update func(last any) any) any {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.v = update(m.v)
	return m.v
}

// BuildingTable indexes v's rows by id; a nil v is an empty table.
func BuildingTable(v *o.BuildingsSnapshot) Buildings {
	return NewBuildings(v.GetBuildings()...)
}

// Row is ref's canonical row, false when the table does not hold it or
// the row's service or settings are malformed.
// Entity is the head of ref's row, nil when the table does not hold it.
func (b Buildings) Entity(ref Reference) *o.EntityRef {
	if ref == nil {
		return nil
	}
	row, _ := b.Get(ref.GetId())
	return row.GetBuilding()
}

func (b Buildings) Row(ref Reference) (*o.BuildingState, bool) {
	row, ok := b.Get(ref.GetId())
	if !ok || validBuildingService(row.GetService()) != nil || row.GetSettings() == nil {
		return nil, false
	}
	return row, true
}

// validBuildingService checks a canonical row's service state: finite
// quantities, valid fuel definitions and network id, and a "fuel" issue
// only where no fuel is reported.
func validBuildingService(s *o.BuildingServiceState) error {
	if s == nil {
		return contract("building row without service")
	}
	for _, value := range []*float64{s.Fuel, s.TargetFuel} {
		if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0 || *value > 1e12) {
			return contract("invalid building fuel quantity")
		}
	}
	if v := s.PowerOutputW; v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0) || math.Abs(*v) > 1e12) {
		return contract("invalid building power output")
	}
	if s.PowerNetId != nil && (validID(s.GetPowerNetId()) != nil || s.Connected != nil && !s.GetConnected()) {
		return contract("invalid building power network")
	}
	defs := map[string]bool{}
	for _, d := range s.AllowedFuelDefs {
		if validID(d) != nil || defs[d] {
			return contract("invalid building fuel definition")
		}
		defs[d] = true
	}
	if err := pawnsIssues(s.Issues, s.ProtoReflect()); err != nil {
		return err
	}
	for _, issue := range s.Issues {
		if issue.GetField() != "fuel" || issue.GetUnavailable().GetReason() != c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE || s.Fuel != nil || s.TargetFuel != nil || len(s.AllowedFuelDefs) > 0 {
			return contract("conflicting building fuel availability")
		}
	}
	return nil
}

// FrameBuildings is the building table of the newest frame (the bundle's
// player building list), or that list read over GABP without a stream.
func (caller *Client) FrameBuildings(ctx context.Context, identity *c.Identity) (Buildings, error) {
	if err := ValidateIdentity(identity); err != nil {
		return Buildings{}, err
	}
	if caller.frames != nil {
		held, err := caller.frameHeld(ctx, frameBuildingsMethod, identity)
		return held.buildings, err
	}
	request := buildingsListRequest(identity)
	reply := &o.ListBuildingsReply{}
	served, err := caller.frameRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return Buildings{}, err
	}
	if !served {
		raw, err := caller.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
		if err != nil {
			return Buildings{}, err
		}
		switch v := reply.Outcome.(type) {
		case *o.ListBuildingsReply_Failure:
			return Buildings{}, failure(v.Failure, raw)
		case *o.ListBuildingsReply_Unavailable:
			return Buildings{}, unavailable(v.Unavailable, raw)
		}
	}
	observed := reply.GetObserved()
	if observed == nil || ValidateContext(observed.Context) != nil || !sameIdentity(observed.Context.Identity, identity) {
		return Buildings{}, contract("building table without the expected context")
	}
	return BuildingTable(observed), nil
}
