package observation

import (
	"maps"
	"reflect"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The construction census is derived from the building table: a
// frame-to-frame state that applies only the rows that changed between two
// table versions (bridge.Table.Changed), instead of projecting every
// building each step. Its outputs are rebuilt only when a projection
// changed (a hit-point change alters none), else the last ones stand.

// constructionState is the census derived from rows.
type constructionState struct {
	rows  bridge.Buildings
	built map[string]builtEntry
	sites map[string]siteEntry
	// deficit sums the standing sites' needs; badNeeds are the sites whose
	// need is malformed (the deficit is then unknown).
	deficit  map[policy.Resource]int64
	badNeeds map[string]bool
	// dirty marks a changed built or site projection since the outputs.
	dirty bool
	// outputs
	census      domain.Fact[policy.CurrentConstruction]
	err         error
	deficitFact domain.Fact[map[policy.Resource]int64]
}

type builtEntry struct {
	building policy.CurrentBuilding
	known    bool
	err      error
}

type siteEntry struct {
	site policy.ConstructionSite
	err  error
}

// ConstructionFromCensus is the colony construction census with its
// standing sites, and the construction deficit, of a frame's building
// table. With the stream's memo it applies only the rows changed since the
// last frame; without one (a recording) it projects the whole table.
func ConstructionFromCensus(census *bridge.BuildingCensus) (domain.Fact[policy.CurrentConstruction], domain.Fact[map[policy.Resource]int64], error) {
	var construction domain.Fact[policy.CurrentConstruction]
	var deficit domain.Fact[map[policy.Resource]int64]
	var err error
	update := func(last any) any {
		prev, _ := last.(*constructionState)
		state := prev.next(census.Rows)
		construction, deficit, err = state.census, copyDeficit(state.deficitFact), state.err
		return state
	}
	if census.Memo != nil {
		census.Memo.Update(update)
	} else {
		update(nil)
	}
	return construction, deficit, err
}

func copyDeficit(f domain.Fact[map[policy.Resource]int64]) domain.Fact[map[policy.Resource]int64] {
	if m, known := f.Value(); known {
		return domain.Known(maps.Clone(m))
	}
	return f
}

// next applies rows to the state derived from the previous version (nil
// derives from scratch) and returns the state at rows. The outputs are
// fresh values: a state is only ever mutated inside the memo's lock.
func (s *constructionState) next(rows bridge.Buildings) *constructionState {
	if s == nil {
		s = &constructionState{built: map[string]builtEntry{}, sites: map[string]siteEntry{}, deficit: map[policy.Resource]int64{}, badNeeds: map[string]bool{}, dirty: true}
		for id, row := range rows.All() {
			s.set(id, row)
		}
	} else {
		prev := s.rows
		rows.Changed(prev.Table, func(id string, row *o.BuildingState) {
			if old, ok := prev.Get(id); ok {
				s.dropNeeds(id, old)
			}
			s.set(id, row)
		}, func(id string) {
			if old, ok := prev.Get(id); ok {
				s.dropNeeds(id, old)
			}
			s.unset(id)
		})
	}
	s.rows = rows
	if s.dirty {
		s.rebuild()
	}
	s.rebuildDeficit()
	return s
}

// set projects row into the state, marking the lists dirty only when the
// row's projection differs from the one held.
func (s *constructionState) set(id string, row *o.BuildingState) {
	switch row.GetStatus() {
	case o.BuildingStatus_BUILDING_STATUS_BUILT:
		b, known, err := currentBuilding(row)
		next := builtEntry{b, known, err}
		if old, ok := s.built[id]; !ok || !reflect.DeepEqual(old, next) {
			s.dirty = true
		}
		s.built[id] = next
		delete(s.sites, id)
	case o.BuildingStatus_BUILDING_STATUS_BLUEPRINT, o.BuildingStatus_BUILDING_STATUS_FRAME:
		if _, ok := s.built[id]; ok {
			delete(s.built, id)
			s.dirty = true
		}
		if site, ok, err := constructionSite(row); ok || err != nil {
			next := siteEntry{site, err}
			if old, had := s.sites[id]; !had || !reflect.DeepEqual(old, next) {
				s.dirty = true
			}
			s.sites[id] = next
		} else {
			s.unsetSite(id)
		}
		needs, valid := siteNeeds(row)
		if !valid {
			s.badNeeds[id] = true
		}
		for _, n := range needs {
			s.deficit[n.resource] += n.count
		}
	default:
		s.unset(id)
	}
}

// unset forgets id's projections.
func (s *constructionState) unset(id string) {
	if _, ok := s.built[id]; ok {
		delete(s.built, id)
		s.dirty = true
	}
	s.unsetSite(id)
}

func (s *constructionState) unsetSite(id string) {
	if _, ok := s.sites[id]; ok {
		delete(s.sites, id)
		s.dirty = true
	}
}

// dropNeeds removes a replaced or removed row's share of the deficit.
func (s *constructionState) dropNeeds(id string, row *o.BuildingState) {
	delete(s.badNeeds, id)
	needs, _ := siteNeeds(row)
	for _, n := range needs {
		if s.deficit[n.resource] -= n.count; s.deficit[n.resource] == 0 {
			delete(s.deficit, n.resource)
		}
	}
}

// rebuild recomputes the outputs from the projections, in id order.
func (s *constructionState) rebuild() {
	s.dirty = false
	s.err = nil
	s.census = domain.Unknown[policy.CurrentConstruction]()
	r := policy.CurrentConstruction{Colony: true, Requested: []string{}, Buildings: []policy.CurrentBuilding{}}
	unknown := false
	for _, id := range slices.Sorted(maps.Keys(s.built)) {
		e := s.built[id]
		switch {
		case e.err != nil:
			s.err = e.err
			return
		case !e.known:
			unknown = true
		default:
			r.Buildings = append(r.Buildings, e.building)
		}
	}
	if !unknown {
		r.Sites = []policy.ConstructionSite{}
		for _, id := range slices.Sorted(maps.Keys(s.sites)) {
			e := s.sites[id]
			if e.err != nil {
				s.err = e.err
				s.census = domain.Unknown[policy.CurrentConstruction]()
				return
			}
			r.Sites = append(r.Sites, e.site)
		}
		s.census = domain.Known(r)
	}
}

func (s *constructionState) rebuildDeficit() {
	s.deficitFact = domain.Unknown[map[policy.Resource]int64]()
	if len(s.badNeeds) == 0 {
		s.deficitFact = domain.Known(maps.Clone(s.deficit))
	}
}
