package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// The flight recorder names each skipped Odyssey offer once per quest and
// reason (#1717), as a WARN routine_skip row (#2066; the quest detail rides in
// the row attrs).
func TestRounderLogsOdysseySkipsOncePerQuest(t *testing.T) {
	rows := telemetrytest.Install(t)
	r := &Rounder{}
	offer := func(id, script string, class domain.Fact[policy.QuestClass]) policy.JoinerOffer {
		return policy.JoinerOffer{Quest: domain.QuestID(id), ScriptDef: script, State: "NotYetAccepted", CanAccept: true, Class: class}
	}
	ship := domain.Known(policy.QuestClass{Scope: policy.QuestScopeShipOnly, SpaceLayer: "Orbit"})
	facts := func(offers ...policy.JoinerOffer) policy.RoundsFacts {
		return policy.RoundsFacts{QuestOffers: domain.Known(offers)}
	}
	ctx := context.Background()
	r.logOdysseySkips(ctx, policy.RoundsFacts{})
	r.logOdysseySkips(ctx, facts(offer("Quest_1", "MechanoidSignal", domain.Known(policy.QuestClass{Scope: policy.QuestScopeGround}))))
	if len(rows.All()) != 0 {
		t.Fatalf("logged without a skip: %+v", rows.All())
	}
	r.logOdysseySkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship)))
	r.logOdysseySkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship)))
	r.logOdysseySkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship), offer("Quest_12", "SurveySite", domain.Unknown[policy.QuestClass]())))
	got := rows.Of("routine_skip")
	if len(got) != 2 || len(rows.All()) != 2 ||
		got[0].Context["level"] != "WARN" || got[0].Payload["verdict"] != "skipped" || got[0].Payload["reason"] != "ship_only" || got[0].Payload["target"] != "Quest_9" ||
		got[1].Payload["reason"] != "class_unknown" || got[1].Payload["target"] != "Quest_12" {
		t.Fatalf("rows: %+v", rows.All())
	}
}
