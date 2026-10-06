package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// journalRecoveryShadow files the recovery_shadow row (#2300): the removals
// the old clearance path admitted this step and the holds it journaled in the
// review, compared with the review's recovery queue. It runs beside the old
// path, which keeps executing until #2301.
func journalRecoveryShadow(call context.Context, queue policy.RecoveryQueue, holds []policy.ClearanceHold, admitted []policy.ClearanceTarget) {
	old := policy.RecoveryOld{Holds: holds}
	for _, row := range admitted {
		old.Admitted = append(old.Admitted, row.EntityID)
	}
	telemetry.Decide(call, recoveryShadowDecision(policy.CompareRecovery(old, queue)))
}

// recoveryShadowDecision is the recovery_shadow row: verdict agree, expected
// (every divergence is one the epic removes) or diverged; reason the first
// unexplained class; attrs the old admitted ids, the queue's working batch,
// the loot entries not compared, tier1 (always "unsupplied": the queue gets no
// room obstruction ids yet) and each divergence.
func recoveryShadowDecision(s policy.RecoveryShadow) telemetry.Decision {
	verdict, reason := "agree", "none"
	if len(s.Divergences) > 0 {
		verdict = "expected"
	}
	rows := make([]map[string]any, len(s.Divergences))
	for i, d := range s.Divergences {
		rows[i] = map[string]any{"id": d.ID, "class": string(d.Class), "expected": d.Expected, "old": d.Old, "queue": d.Queue}
		if !d.Expected && verdict != "diverged" {
			verdict, reason = "diverged", string(d.Class)
		}
	}
	return telemetry.Decision{Kind: "recovery_shadow", Component: "clock-scheduler", Verdict: verdict, Reason: reason, Target: "clearance",
		Attrs: map[string]any{"old_admitted": s.OldAdmitted, "queue_batch": s.QueueBatch, "loot": s.Loot, "tier1": "unsupplied", "divergences": rows}}
}
