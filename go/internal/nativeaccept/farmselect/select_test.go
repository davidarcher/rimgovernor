package farmselect

import (
	"strings"
	"testing"
)

const trace = `[clock-scheduler] Fields plan: Plant_Rice needed=671 urgent=true
[clock-scheduler] Fields select: kind=outdoor crop=Plant_Rice cells=224 buildings=0 | outdoor Plant_Rice needed=671 urgent=true buildings=0
 outdoor Plant_Rice needed=671 cells=224 score=0.0210 yield=22.4000 travel=-3.9000 hauling=-1.3000 perimeter=-2.5600 fragment=-0.7500 net 14.0900/day over 15 patches
 outdoor Plant_Potato needed=0 cells=0 score=0.0000 season too short
 greenhouse-new Plant_Rice needed=671 cells=0 score=0.0000 sun lamp unavailable
[clock-scheduler] step done: err=<nil>
2026-09-18T19:46:03.123Z tick=4200 DEBUG [clock-scheduler] Fields select: kind=greenhouse-reuse crop=Plant_Corn cells=59 buildings=0 | greenhouse-reuse Plant_Corn needed=55 urgent=false buildings=0
 greenhouse-reuse Plant_Corn needed=55 cells=59 score=0.0411 yield=5.9000 travel=-2.3385 net 2.2600/day over 5 patches
`

func TestParseAndCheck(t *testing.T) {
	selections, err := Parse(strings.NewReader(trace))
	if err != nil || len(selections) != 2 {
		t.Fatal(selections, err)
	}
	first := selections[0]
	if first.Kind != "outdoor" || first.Crop != "Plant_Rice" || first.Cells != 224 || !first.Urgent || len(first.Candidates) != 3 {
		t.Fatalf("%+v", first)
	}
	if c := first.Candidates[0]; c.Terms["yield"] != 22.4 || c.Terms["fragment"] != -0.75 || c.Reason != "net 14.0900/day over 15 patches" {
		t.Fatalf("%+v", c)
	}
	if c := first.Candidates[2]; c.Kind != "greenhouse-new" || c.Reason != "sun lamp unavailable" || c.Terms != nil {
		t.Fatalf("%+v", c)
	}
	if _, err := Check(selections, Expectation{Kind: "outdoor"}); err == nil {
		t.Fatal("second selection's kind was accepted")
	}
	last, err := Check(selections[:1], Expectation{Kind: "outdoor", Crop: "Plant_Rice", MinCells: 100})
	if err != nil || last.Cells != 224 {
		t.Fatal(last, err)
	}
	if _, err := Check(nil, Expectation{}); err == nil {
		t.Fatal("empty trace passed")
	}
	// A winner without terms, or a loser without a reason, fails.
	broken := strings.Replace(trace, " yield=22.4000 travel=-3.9000 hauling=-1.3000 perimeter=-2.5600 fragment=-0.7500 net 14.0900/day over 15 patches", "", 1)
	selections, _ = Parse(strings.NewReader(broken))
	if _, err := Check(selections[:1], Expectation{}); err == nil {
		t.Fatal("winner without breakdown passed")
	}
	broken = strings.Replace(trace, " season too short", "", 1)
	selections, _ = Parse(strings.NewReader(broken))
	if _, err := Check(selections[:1], Expectation{}); err == nil {
		t.Fatal("loser without reason passed")
	}
}
