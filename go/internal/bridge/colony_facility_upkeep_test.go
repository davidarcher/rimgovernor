package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestFacilityUpkeepWirePresenceGeometryAndCompleteness(t *testing.T) {
	makeFacts := func() *o.UpkeepFacts {
		v := upkeepWire()
		v.Structures[0].Flammability = proto.Float64(1)
		v.HomeCoverage = &o.HomeCoverageSection{Outcome: &o.HomeCoverageSection_Observed{Observed: &o.HomeCoverageFacts{Revision: proto.Int64(2), Targets: []*o.HomeCoverageTarget{{Id: proto.String("wall"), ShapeToken: proto.String("shape"), MissingCells: proto.Uint32(1), ExcludedCells: proto.Uint32(1), Cells: []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}}}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
		return v
	}
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(makeFacts(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{"flammability", "revision", "shape", "count", "duplicate", "bounds", "page", "issue"} {
		t.Run(field, func(t *testing.T) {
			v := makeFacts()
			h := v.HomeCoverage.GetObserved()
			switch field {
			case "flammability":
				v.Structures[0].Flammability = proto.Float64(math.NaN())
			case "revision":
				h.Revision = nil
			case "shape":
				h.Targets[0].ShapeToken = proto.String("")
			case "count":
				h.Targets[0].ExcludedCells = proto.Uint32(2)
			case "duplicate":
				h.Targets[0].Cells = append(h.Targets[0].Cells, h.Targets[0].Cells[0])
			case "bounds":
				h.Targets[0].Cells[0].X = proto.Int32(50)
			case "page":
				h.Completeness.Unreadable = proto.Uint64(1)
			case "issue":
				v.Issues = []*o.ReadIssue{{Field: proto.String("home_coverage"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
			}
			if err := validateDirectUpkeep(v, size, 3); err == nil {
				t.Fatal(v)
			}
		})
	}
	v := makeFacts()
	v.HomeCoverage.GetObserved().Targets[0] = &o.HomeCoverageTarget{Id: proto.String("wall"), Blocker: proto.String("Native geometry unavailable")}
	v.Structures[0].Flammability = nil
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("unknown fields fabricated or rejected", err)
	}
}
