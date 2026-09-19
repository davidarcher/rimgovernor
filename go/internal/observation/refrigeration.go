package observation

import (
	"context"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// RefrigerationSource reads exact cooler buildings so MaintainRefrigeration
// can see each cooler's rotation, target setpoint and CAS token. Power state
// comes from the same-tick colony power census instead.
type RefrigerationSource interface {
	ReadConstructionBuildings(context.Context, *c.Identity, []string) (*o.ListBuildingsReply, bridge.Result, error)
}

// ReadRefrigerationCoolers joins the colony power census's Cooler rows with a
// typed building read of those exact IDs. A cooler the building read does not
// return, or returns without temperature control, makes the census unknown:
// the planner must not build a second cooler on a fact it cannot see.
func ReadRefrigerationCoolers(ctx context.Context, source RefrigerationSource, expected Identity, topology domain.Fact[policy.PowerTopology]) (domain.Fact[[]policy.RefrigerationCooler], bridge.Result, error) {
	identity := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	v, known := topology.Value()
	if !known {
		return domain.Unknown[[]policy.RefrigerationCooler](), bridge.Result{}, nil
	}
	rows := map[string]policy.RefrigerationCooler{}
	var ids []string
	for _, b := range v.Buildings {
		if b.Definition != "Cooler" {
			continue
		}
		rows[b.ID] = policy.RefrigerationCooler{ID: b.ID, Position: b.Cell, Connected: b.Connected, PowerOn: b.Powered}
		ids = append(ids, b.ID)
	}
	if len(ids) == 0 {
		return domain.Known([]policy.RefrigerationCooler{}), bridge.Result{}, nil
	}
	sort.Strings(ids)
	if len(ids) > 256 {
		return domain.Unknown[[]policy.RefrigerationCooler](), bridge.Result{}, nil
	}
	reply, receipt, err := source.ReadConstructionBuildings(ctx, identity, ids)
	if err != nil {
		return domain.Unknown[[]policy.RefrigerationCooler](), receipt, err
	}
	observed := reply.GetObserved()
	if observed == nil || len(observed.Buildings) != len(ids) {
		return domain.Unknown[[]policy.RefrigerationCooler](), receipt, nil
	}
	coolers := make([]policy.RefrigerationCooler, 0, len(ids))
	for _, row := range observed.Buildings {
		cooler, ok := rows[row.GetBuilding().GetId()]
		if !ok {
			return domain.Unknown[[]policy.RefrigerationCooler](), receipt, nil
		}
		cooler.Rotation = domain.Rotation(strings.ToLower(row.GetRotation()))
		settings := row.GetSettings()
		if settings != nil {
			cooler.Target = optional(settings.TargetTemperatureC)
			if settings.Snapshot != nil && settings.Snapshot.GetEntityId() == cooler.ID {
				cooler.Token = settings.Snapshot.GetToken()
			}
		}
		coolers = append(coolers, cooler)
	}
	sort.Slice(coolers, func(i, j int) bool { return coolers[i].ID < coolers[j].ID })
	return domain.Known(coolers), receipt, nil
}

// RefrigerationFacts assembles the method's observation from a rooms
// routine reading (rooms with cells, planning definitions, site cells) and
// the cooler census.
func RefrigerationFacts(projection ColonyProjection, coolers domain.Fact[[]policy.RefrigerationCooler]) domain.Fact[policy.RefrigerationObservation] {
	temperature, tk := projection.Rooms.Value()
	rows, ck := coolers.Value()
	if !tk || !ck {
		return domain.Unknown[policy.RefrigerationObservation]()
	}
	result := policy.RefrigerationObservation{Rooms: temperature.Rooms, Coolers: rows, Cells: projection.Cells, CoolerAvailable: domain.Unknown[bool](), Blackout: domain.Unknown[bool]()}
	if topology, known := projection.PowerPlanning.Value(); known {
		result.Blackout = topology.Blackout
	}
	for _, d := range projection.Definitions {
		if d.Name == "Cooler" {
			result.CoolerAvailable = d.Available
		}
	}
	return domain.Known(result)
}
