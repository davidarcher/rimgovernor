package nativeaccept

import (
	"context"
	"fmt"
	"strings"
)

// FreezeNeedsTool pins colonist needs at maximum
// (scripts/fixtures/FreezeNeedsFixture.cs); every fixture build carries it.
const FreezeNeedsTool = "test/freeze_needs"

// FreezeNeeds pins every free colonist need except keep (NeedDef names such
// as Food, Rest, Joy) for the rest of the game, so a harness whose assertion
// is not about eating, sleeping or mood never loses a colonist to them. The
// reply names what was frozen; record it on the report. A missing op is an
// error: harnesses that freeze need a fixture build anyway.
func FreezeNeeds(ctx context.Context, h *Harness, names []string, keep ...string) (map[string]any, error) {
	if names == nil {
		var err error
		if names, err = h.Discovery(ctx); err != nil {
			return nil, err
		}
	}
	if !Contains(names, FreezeNeedsTool) {
		return nil, fmt.Errorf("%s is not installed: build the mod with a fixture", FreezeNeedsTool)
	}
	reply, err := h.Call(ctx, "freeze-needs", FreezeNeedsTool, map[string]any{"action": "apply", "keep": strings.Join(keep, ",")})
	if err != nil {
		return nil, err
	}
	if ok, _ := AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s refused: %#v", FreezeNeedsTool, reply)
	}
	return reply, nil
}

// RecordFrozenNeeds freezes every colonist need but keep and records the
// reply under report["frozen_needs"]: the one call every serve-driven
// harness makes once its save is loaded and staged, so colonists never
// eat, sleep or break through an assertion that is not about them (#131).
// LiveNeeds in keep skips the freeze and leaves frozen_needs unset.
func RecordFrozenNeeds(ctx context.Context, h *Harness, names []string, report Report, keep ...NeedDef) error {
	kept := make([]string, 0, len(keep))
	for _, need := range keep {
		if need == LiveNeeds {
			return nil
		}
		kept = append(kept, string(need))
	}
	frozen, err := FreezeNeeds(ctx, h, names, kept...)
	if err != nil {
		return err
	}
	report["frozen_needs"] = frozen
	return nil
}
