package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// The flight recorder names each skipped Odyssey offer once per quest and
// reason, as a WARN routine_skip row (the quest detail rides in
// the row attrs).
func TestRounderLogsQuestSkipsOncePerQuest(t *testing.T) {
	rows := telemetrytest.Install(t)
	r := &Rounder{}
	offer := func(id, script string, class domain.Fact[policy.QuestClass]) policy.JoinerOffer {
		row := policy.JoinerOffer{Quest: domain.QuestID(id), ScriptDef: script, State: "NotYetAccepted", CanAccept: true, Class: class}
		if _, known := class.Value(); known {
			row.Profile = domain.Known(policy.QuestFamilyForRoot(script))
		}
		return row
	}
	ship := domain.Known(policy.QuestClass{Scope: policy.QuestScopeShipOnly, SpaceLayer: "Orbit"})
	facts := func(offers ...policy.JoinerOffer) policy.RoundsFacts {
		return policy.RoundsFacts{QuestOffers: domain.Known(offers), QuestColonyCalm: domain.Known(true), QuestSparePawns: domain.Known([]policy.PawnID{"spare"}), QuestColonistsAtHome: domain.Known(4)}
	}
	ctx := context.Background()
	r.logQuestSkips(ctx, policy.RoundsFacts{})
	r.logQuestSkips(ctx, facts(offer("Quest_1", "MechanoidSignal", domain.Known(policy.QuestClass{Scope: policy.QuestScopeGround}))))
	if len(rows.All()) != 0 {
		t.Fatalf("logged without a skip: %+v", rows.All())
	}
	r.logQuestSkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship)))
	r.logQuestSkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship)))
	r.logQuestSkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship), offer("Quest_12", "SurveySite", domain.Unknown[policy.QuestClass]())))
	got := rows.Of("routine_skip")
	if len(got) != 2 || len(rows.All()) != 2 ||
		got[0].Context["level"] != "WARN" || got[0].Payload["verdict"] != "skipped" || got[0].Payload["reason"] != "ship_only" || got[0].Payload["target"] != "Quest_9" ||
		got[1].Payload["reason"] != "class_unknown" || got[1].Payload["target"] != "Quest_12" {
		t.Fatalf("rows: %+v", rows.All())
	}
	empire := offer("Quest_20", "Hospitality_Joiners", domain.Known(policy.QuestClass{Scope: policy.QuestScopeOther}))
	empire.FactionID = "Empire"
	empire.FactionHostile = domain.Known(true)
	r.logQuestSkips(ctx, facts(empire))
	r.logQuestSkips(ctx, facts(empire))
	got = rows.Of("routine_skip")
	if len(got) != 3 || got[2].Payload["reason"] != "hostile" {
		t.Fatalf("Empire skip: %+v", got)
	}
}
