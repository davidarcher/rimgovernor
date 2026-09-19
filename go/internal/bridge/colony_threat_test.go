package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func threatFacts() *o.ThreatFacts {
	return &o.ThreatFacts{
		WealthItems: proto.Float64(1200), WealthBuildings: proto.Float64(800), WealthPawns: proto.Float64(5400), WealthTotal: proto.Float64(7400),
		StorytellerWealth: proto.Float64(6200), RaidPoints: proto.Float64(120.5), AdaptationFactor: proto.Float64(1), DifficultyThreatScale: proto.Float64(1),
		ColonistCount: proto.Uint32(3),
		Completeness:  &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
	}
}

// The threat section (#395) is optional on the wire: an absent or
// unavailable section validates and projects to unknown facts, an observed
// one carries every number, and a non-finite or negative number is refused
// the way combatNumber refuses one anywhere else.
func TestColonyThreatSectionValidatesAndProjects(t *testing.T) {
	r := colonyFixture(t).GetObserved()
	id := proto.Clone(r.Context.Identity).(*c.Identity)
	if r.Threat != nil {
		t.Fatal("fixture already carries a threat section")
	}
	if err := ValidateColonyFacts(r, id); err != nil {
		t.Fatal(err)
	}
	if _, known := ProjectColonyThreat(r).RaidPoints.Value(); known {
		t.Fatal("absent section projected a known raid point figure")
	}
	r.Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("Colony wealth and raid points could not be read.")}}}
	if err := ValidateColonyFacts(r, id); err != nil {
		t.Fatal(err)
	}
	if _, known := ProjectColonyThreat(r).WealthTotal.Value(); known {
		t.Fatal("unavailable section projected a known wealth figure")
	}
	r.Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Observed{Observed: threatFacts()}}
	if err := ValidateColonyFacts(r, id); err != nil {
		t.Fatal(err)
	}
	threat := ProjectColonyThreat(r)
	if points, known := threat.RaidPoints.Value(); !known || points != 120.5 {
		t.Fatal(threat)
	}
	if items, known := threat.WealthItems.Value(); !known || items != 1200 {
		t.Fatal(threat)
	}
	if total, known := threat.WealthTotal.Value(); !known || total != 7400 {
		t.Fatal(threat)
	}
	r.Threat.GetObserved().WealthPawns = nil
	if _, known := ProjectColonyThreat(r).WealthPawns.Value(); known {
		t.Fatal("a withheld field projected a known value")
	}
	for name, change := range map[string]func(*o.ThreatFacts){
		"nan":         func(f *o.ThreatFacts) { f.RaidPoints = proto.Float64(math.NaN()) },
		"infinite":    func(f *o.ThreatFacts) { f.WealthTotal = proto.Float64(math.Inf(1)) },
		"negative":    func(f *o.ThreatFacts) { f.WealthItems = proto.Float64(-1) },
		"partial":     func(f *o.ThreatFacts) { f.Completeness.Page.Complete = proto.Bool(false) },
		"unavailable": func(f *o.ThreatFacts) { f.Issues = []*o.ReadIssue{{Field: proto.String("raid_points")}} },
	} {
		t.Run(name, func(t *testing.T) {
			facts := threatFacts()
			change(facts)
			r.Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Observed{Observed: facts}}
			if err := ValidateColonyFacts(r, id); err == nil {
				t.Fatal("malformed threat facts accepted")
			}
		})
	}
	r.Threat = &o.ThreatSection{}
	if err := ValidateColonyFacts(r, id); err == nil {
		t.Fatal("empty threat section accepted")
	}
}
