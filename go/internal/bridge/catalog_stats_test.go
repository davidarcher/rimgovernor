package bridge

import (
	"fmt"
	"math"
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func statTestCatalog() *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	v.ThingDefs = []*d.ThingDef{{DefName: "Apparel_Parka"}, {DefName: "Steel"}, {DefName: "Cloth"}, {DefName: "Beer"}}
	v.StatValues = &o.DefStatTable{
		Stats: []string{"ArmorRating_Sharp", "Insulation_Cold", "MarketValue"},
		Rows: []*o.DefStatRow{
			{DefName: "Apparel_Parka", StuffName: "Steel", Stat: []int32{0, 1, 2}, Value: []float32{0.9, 0, 150.5}, Costs: []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(60)}}},
			{DefName: "Apparel_Parka", StuffName: "Cloth", Stat: []int32{1, 2}, Value: []float32{30, 40}},
			{DefName: "Beer", Stat: []int32{2}, Value: []float32{12}},
		},
	}
	return v
}

// TestDefinitionCatalogStatValues (#1759): the game's stat values decode into
// the cache by (def, stuff, stat); a stat the game did not show, a missing
// row and a missing table are errors, and a malformed table is refused.
func TestDefinitionCatalogStatValues(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(statTestCatalog(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		def, stuff, stat string
		want             float32
	}{{"Apparel_Parka", "Steel", "ArmorRating_Sharp", 0.9}, {"Apparel_Parka", "Steel", "Insulation_Cold", 0}, {"Apparel_Parka", "Cloth", "MarketValue", 40}, {"Beer", "", "MarketValue", 12}} {
		if got, err := catalog.StatValue(c.def, c.stuff, c.stat); err != nil || got != c.want {
			t.Fatalf("%v: got %v, %v", c, got, err)
		}
	}
	if costs, err := catalog.AdjustedCosts("Apparel_Parka", "Steel"); err != nil || len(costs) != 1 || costs[0].GetUnits() != 60 {
		t.Fatalf("costs %v %v", costs, err)
	}
	if costs, err := catalog.AdjustedCosts("Beer", ""); err != nil || len(costs) != 0 {
		t.Fatalf("no-cost def %v %v", costs, err)
	}
	if _, err := catalog.AdjustedCosts("Beer", "Steel"); err == nil {
		t.Fatal("missing row answered")
	}
	for _, c := range [][3]string{{"Apparel_Parka", "Cloth", "ArmorRating_Sharp"}, {"Apparel_Parka", "", "MarketValue"}, {"Beer", "Steel", "MarketValue"}, {"Missing", "", "MarketValue"}} {
		if v, err := catalog.StatValue(c[0], c[1], c[2]); err == nil {
			t.Fatalf("%v answered %v", c, v)
		}
	}
	bare, err := DecodeDefinitionCatalog(catalogReply(authorityTestContext(7)).GetObserved(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := bare.StatValue("Beer", "", "MarketValue"); err == nil || !strings.Contains(err.Error(), "no stat values") {
		t.Fatalf("absent table: %v", err)
	}
	var none *DefinitionCatalog
	if _, err := none.StatValue("Beer", "", "MarketValue"); err == nil {
		t.Fatal("nil catalog answered")
	}
	for _, change := range []string{"unknown-def", "unknown-stuff", "duplicate-row", "length-mismatch", "index-range", "negative-index", "repeated-stat", "repeated-name", "bad-name", "nan", "cost-unknown", "cost-zero"} {
		t.Run(change, func(t *testing.T) {
			v := statTestCatalog()
			rows := v.StatValues.Rows
			switch change {
			case "unknown-def":
				rows[0].DefName = "Missing"
			case "unknown-stuff":
				rows[0].StuffName = "Missing"
			case "duplicate-row":
				rows[1].StuffName = "Steel"
			case "length-mismatch":
				rows[0].Value = rows[0].Value[:2]
			case "index-range":
				rows[0].Stat[0] = 3
			case "negative-index":
				rows[0].Stat[0] = -1
			case "repeated-stat":
				rows[0].Stat[1] = 0
			case "repeated-name":
				v.StatValues.Stats[1] = "MarketValue"
			case "bad-name":
				v.StatValues.Stats[1] = ""
			case "cost-unknown":
				rows[0].Costs[0].DefName = proto.String("Missing")
			case "cost-zero":
				rows[0].Costs[0].Units = proto.Int64(0)
			case "nan":
				rows[0].Value[0] = float32(math.NaN())
			}
			if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
				t.Fatal("malformed stat table accepted")
			}
		})
	}
}

// TestDefinitionCatalogStatTableSize (#1759) sizes a synthesized stat table
// like the game's: 270 stats, 450 stuffed defs with 25 allowed stuffs each and
// 2450 defs without stuff, 40 shown stats and 3 cost entries per row. The game is not available;
// the shape is an estimate of the order, not a measurement of the real reply.
func TestDefinitionCatalogStatTableSize(t *testing.T) {
	const statCount, stuffedDefs, stuffs, plainDefs, shown = 270, 450, 25, 2450, 40
	v := catalogReply(authorityTestContext(7)).GetObserved()
	table := &o.DefStatTable{}
	for i := range statCount {
		table.Stats = append(table.Stats, fmt.Sprintf("StatDefName%d", i))
	}
	row := func(def, stuff string, seed int) *o.DefStatRow {
		r := &o.DefStatRow{DefName: def, StuffName: stuff}
		for k := range shown {
			r.Stat = append(r.Stat, int32((k*statCount/shown+seed)%statCount))
			r.Value = append(r.Value, float32(seed+k)*0.37+1.1)
			if k < 3 {
				r.Costs = append(r.Costs, &o.Quantity{DefName: proto.String("Stuff0"), Units: proto.Int64(int64(k + 1))})
			}
		}
		return r
	}
	for i := range stuffs {
		v.ThingDefs = append(v.ThingDefs, &d.ThingDef{DefName: fmt.Sprintf("Stuff%d", i)})
	}
	for i := range stuffedDefs + plainDefs {
		name := fmt.Sprintf("Thing%d", i)
		v.ThingDefs = append(v.ThingDefs, &d.ThingDef{DefName: name})
		if i >= stuffedDefs {
			table.Rows = append(table.Rows, row(name, "", i))
			continue
		}
		for s := range stuffs {
			table.Rows = append(table.Rows, row(name, fmt.Sprintf("Stuff%d", s), i+s))
		}
	}
	v.StatValues = table
	raw, err := proto.Marshal(table)
	if err != nil {
		t.Fatal(err)
	}
	catalog, err := DecodeDefinitionCatalog(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if got, err := catalog.StatValue("Thing0", "Stuff0", "StatDefName0"); err != nil || got != 1.1 {
		t.Fatalf("lookup %v %v", got, err)
	}
	t.Logf("stat table %d bytes, %d rows (%d stuffed x %d stuffs + %d plain), %d stats", len(raw), len(table.Rows), stuffedDefs, stuffs, plainDefs, statCount)
}
