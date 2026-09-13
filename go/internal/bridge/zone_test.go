package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestZoneExactConfigurationEvidence(t *testing.T) {
	zone, _ := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	token := ZoneConfigurationToken(zone)
	if token != "zone-defa6b500f62968795cd38e729afec1f76043176f93cab6983029e87a4556a9c" {
		t.Fatal("native configuration hash drift", token)
	}
	v := &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{ZoneId: proto.String("zone1"), Present: proto.Bool(true), ListedCellCount: proto.Int32(2), GridCellCount: proto.Int32(2), PhantomCellCount: proto.Int32(0), ChangedCells: proto.Int32(2), Snapshot: &r.SnapshotEvidence{EntityId: proto.String("zone1"), BeforeToken: proto.String("map-token"), AfterToken: proto.String(token)}}}}
	for _, cell := range zone.Cells() {
		v.GetZone().Cells = append(v.GetZone().Cells, &r.CellResult{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Accepted: proto.Bool(true)})
	}
	if matches, err := ZoneMatches(v, zone, "map-token"); err != nil || !matches {
		t.Fatal(matches, err)
	}
	for _, change := range []func(*r.EffectEvidence){func(v *r.EffectEvidence) { v.GetZone().Snapshot.AfterToken = proto.String("other-crop") }, func(v *r.EffectEvidence) { v.GetZone().GridCellCount = proto.Int32(1) }, func(v *r.EffectEvidence) { v.GetZone().Cells[1].Cell.X = proto.Int32(2) }, func(v *r.EffectEvidence) { v.GetZone().Snapshot.BeforeToken = proto.String("foreign-map") }} {
		copy := proto.Clone(v).(*r.EffectEvidence)
		change(copy)
		if matches, _ := ZoneMatches(copy, zone, "map-token"); matches {
			t.Fatal("foreign/incomplete zone")
		}
	}
	v.GetZone().Cells[1] = v.GetZone().Cells[0]
	if _, err := ZoneMatches(v, zone, "map-token"); err == nil {
		t.Fatal("duplicate cells")
	}
}

func TestZoneConfigurationBranchesOnKind(t *testing.T) {
	cells := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	growing, _ := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", cells)
	stockpile, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, cells)

	g := ZoneConfiguration(growing)
	if g.GetType() != op.ZoneType_ZONE_TYPE_GROWING || g.Stockpile != nil || g.GetGrowing().GetPlantDef() != "Plant_Rice" || !g.GetGrowing().GetAllowSow() || !g.GetGrowing().GetAllowCut() {
		t.Fatal("unexpected growing zone configuration", g)
	}
	s := ZoneConfiguration(stockpile)
	if s.GetType() != op.ZoneType_ZONE_TYPE_STOCKPILE || s.Growing != nil || s.GetStockpile().GetPriority() != op.StoragePriority_STORAGE_PRIORITY_IMPORTANT || s.GetStockpile().GetPreset() != op.FilterPreset_FILTER_PRESET_FOOD {
		t.Fatal("unexpected stockpile zone configuration", s)
	}
	if s.GetLabel() != "RimGovernor food storage" || g.GetLabel() != "RimGovernor crops" {
		t.Fatal("unexpected zone labels", g.GetLabel(), s.GetLabel())
	}
	if ZoneConfigurationToken(growing) == ZoneConfigurationToken(stockpile) {
		t.Fatal("expected distinct configuration hashes for distinct zone kinds")
	}
}

func TestStockpileZoneExactConfigurationEvidence(t *testing.T) {
	zone, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	token := ZoneConfigurationToken(zone)
	v := &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{ZoneId: proto.String("zone1"), Present: proto.Bool(true), ListedCellCount: proto.Int32(2), GridCellCount: proto.Int32(2), PhantomCellCount: proto.Int32(0), ChangedCells: proto.Int32(2), Snapshot: &r.SnapshotEvidence{EntityId: proto.String("zone1"), BeforeToken: proto.String("map-token"), AfterToken: proto.String(token)}}}}
	for _, cell := range zone.Cells() {
		v.GetZone().Cells = append(v.GetZone().Cells, &r.CellResult{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Accepted: proto.Bool(true)})
	}
	if matches, err := ZoneMatches(v, zone, "map-token"); err != nil || !matches {
		t.Fatal(matches, err)
	}
	// Native leaves AfterToken hashed for a different desired configuration
	// (e.g. it could not confirm the live filter matches the requested preset
	// exactly); readback must not paper over that with geometry alone.
	other, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 0, Z: 0}, {X: 2, Z: 0}})
	v.GetZone().Cells[1].Cell.X = proto.Int32(2)
	if matches, _ := ZoneMatches(v, other, "map-token"); matches {
		t.Fatal("expected mismatch against stale configuration hash")
	}
}
