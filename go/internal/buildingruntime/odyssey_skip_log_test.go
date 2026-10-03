package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// The service log names each skipped Odyssey offer once per quest and
// reason (#1717), at warn level.
func TestRoutineReviewerLogsOdysseySkipsOncePerQuest(t *testing.T) {
	var out bytes.Buffer
	previous := slog.Default()
	slog.SetDefault(telemetry.New(&out, slog.LevelInfo, nil))
	defer slog.SetDefault(previous)
	r := &RoutineReviewer{}
	offer := func(id, script string, class domain.Fact[policy.QuestClass]) policy.JoinerOffer {
		return policy.JoinerOffer{Quest: domain.QuestID(id), ScriptDef: script, State: "NotYetAccepted", CanAccept: true, Class: class}
	}
	ship := domain.Known(policy.QuestClass{Scope: policy.QuestScopeShipOnly, SpaceLayer: "Orbit"})
	facts := func(offers ...policy.JoinerOffer) policy.RoutineFacts {
		return policy.RoutineFacts{QuestOffers: domain.Known(offers)}
	}
	ctx := context.Background()
	r.logOdysseySkips(ctx, policy.RoutineFacts{})
	r.logOdysseySkips(ctx, facts(offer("Quest_1", "MechanoidSignal", domain.Known(policy.QuestClass{Scope: policy.QuestScopeGround}))))
	if out.Len() != 0 {
		t.Fatalf("logged without a skip: %s", out.String())
	}
	r.logOdysseySkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship)))
	r.logOdysseySkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship)))
	r.logOdysseySkips(ctx, facts(offer("Quest_9", "OrbitalFugitive", ship), offer("Quest_12", "SurveySite", domain.Unknown[policy.QuestClass]())))
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 2 || !strings.Contains(lines[0], "WARN") || !strings.Contains(lines[0], "[routine] odyssey quest skipped Quest_9 OrbitalFugitive: ship_only (Orbit)") ||
		!strings.Contains(lines[1], "Quest_12 SurveySite: class_unknown") {
		t.Fatalf("log:\n%s", out.String())
	}
}
