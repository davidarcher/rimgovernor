package policy

import (
	"slices"
	"strings"
)

// The shadow comparison (#2300, epic #2291): per review, what the old
// clearance path decided next to what the recovery queue decided, as a set of
// per-thing divergences, each tagged expected (the epic removes that
// behaviour) or unexplained (a release reviewer must account for it). Only
// salvage things are compared; the loot path's decisions are not in the
// clearance planner, so loot entries are counted, not compared. Tier 1 is not
// supplied to the queue yet (the room obstruction ids live with the building
// planners), so a comparison cannot tell a room obstruction's rank from the
// rest: the journal row carries tier1 "unsupplied" for it.

// ShadowClass names how one thing's old and queue decisions differ.
type ShadowClass string

const (
	// ShadowOrder: both admit, the queue's first thing is not the old one.
	ShadowOrder ShadowClass = "order"
	// ShadowBatch (expected): the queue works the thing in its batch; the old
	// path admitted one removal per step.
	ShadowBatch ShadowClass = "batch"
	// ShadowHomeSplit (expected): the old path held it outside Home.
	ShadowHomeSplit ShadowClass = "home_split"
	// ShadowDemandGone (expected): the old path held it for demand.
	ShadowDemandGone ShadowClass = "demand_hold_gone"
	// ShadowReachGone (expected): the old path held it at a reach stage.
	ShadowReachGone ShadowClass = "reach_stage_gone"
	// ShadowStorageThrottle (expected): the queue defers it for missing
	// storage; the old path went ahead.
	ShadowStorageThrottle ShadowClass = "storage_throttle"
	// ShadowQueueHolds: the old path admitted or left it, the queue holds it.
	ShadowQueueHolds ShadowClass = "queue_holds"
	// ShadowQueueDefers: the old path admitted it, the queue defers it for
	// labor.
	ShadowQueueDefers ShadowClass = "queue_defers"
	// ShadowHoldReason: both hold it, for different reasons.
	ShadowHoldReason ShadowClass = "hold_reason"
	// ShadowAdmitsHeld: the old path held it for a reason the queue does not
	// know (a safety word or an unlisted one) and the queue works it.
	ShadowAdmitsHeld ShadowClass = "admits_held"
	// ShadowQueueMissing: a thing the old path named is not in the queue.
	ShadowQueueMissing ShadowClass = "queue_missing"
)

// Expected is true for the classes the epic removes on purpose.
func (c ShadowClass) Expected() bool {
	switch c {
	case ShadowBatch, ShadowHomeSplit, ShadowDemandGone, ShadowReachGone, ShadowStorageThrottle:
		return true
	}
	return false
}

// RecoveryOld is the old clearance path's decision: the removals it
// admitted this step in order, and every hold it journaled.
type RecoveryOld struct {
	Admitted []string
	Holds    []ClearanceHold
}

// ShadowDivergence is one thing the two decisions differ on. Old and Queue
// are the decisions as words: "admitted", "unlisted", "held:<reason>" for the
// old path; the queue's status, with ":<reason>" when it has one.
type ShadowDivergence struct {
	ID       string
	Class    ShadowClass
	Expected bool
	Old      string
	Queue    string
}

// RecoveryShadow is the comparison: the old admitted set and the queue's
// working set (admitted, then queued, in rank order) with the divergences.
type RecoveryShadow struct {
	OldAdmitted []string
	QueueBatch  []string
	Divergences []ShadowDivergence
	// Loot counts the queue's loot entries, which are not compared.
	Loot int
}

// Unexplained counts the divergences that are not expected.
func (s RecoveryShadow) Unexplained() int {
	n := 0
	for _, d := range s.Divergences {
		if !d.Expected {
			n++
		}
	}
	return n
}

// CompareRecovery builds the divergences of the old path's decision and the
// queue's, over the queue's salvage entries (rank order) and then the old
// path's things the queue lacks.
func CompareRecovery(old RecoveryOld, q RecoveryQueue) RecoveryShadow {
	s := RecoveryShadow{OldAdmitted: slices.Clone(old.Admitted)}
	held := make(map[string]string, len(old.Holds))
	for _, h := range old.Holds {
		held[h.Target] = h.Reason
	}
	seen := map[string]bool{}
	add := func(id string, class ShadowClass, oldWord, queueWord string) {
		s.Divergences = append(s.Divergences, ShadowDivergence{ID: id, Class: class, Expected: class.Expected(), Old: oldWord, Queue: queueWord})
	}
	for _, e := range q.Entries {
		if e.Kind != RemoteSalvage {
			s.Loot++
			continue
		}
		seen[e.ID] = true
		works := e.Status == RecoveryAdmitted || e.Status == RecoveryQueued
		if works {
			s.QueueBatch = append(s.QueueBatch, e.ID)
		}
		queueWord := string(e.Status)
		if e.Reason != "" {
			queueWord += ":" + e.Reason
		}
		switch reason, isHeld := held[e.ID]; {
		case slices.Contains(old.Admitted, e.ID):
			switch {
			case e.Status == RecoveryAdmitted:
			case e.Status == RecoveryQueued:
				// Behind another thing: an order difference once, at the head.
			case e.Status == RecoveryHeld:
				add(e.ID, ShadowQueueHolds, "admitted", queueWord)
			case e.Reason == RemoteHoldMissingStorage:
				add(e.ID, ShadowStorageThrottle, "admitted", queueWord)
			default:
				add(e.ID, ShadowQueueDefers, "admitted", queueWord)
			}
		case isHeld:
			oldWord := "held:" + reason
			switch {
			case e.Status == RecoveryHeld && e.Reason == reason:
			case e.Status == RecoveryHeld:
				add(e.ID, ShadowHoldReason, oldWord, queueWord)
			case !works:
			default:
				add(e.ID, shadowReleased(reason), oldWord, queueWord)
			}
		default:
			switch {
			case works:
				add(e.ID, ShadowBatch, "unlisted", queueWord)
			case e.Status == RecoveryHeld:
				add(e.ID, ShadowQueueHolds, "unlisted", queueWord)
			}
		}
	}
	if len(old.Admitted) > 0 && q.Admitted != "" && q.Admitted != old.Admitted[0] && seen[old.Admitted[0]] {
		add(old.Admitted[0], ShadowOrder, "admitted", "first:"+q.Admitted)
	}
	for _, id := range old.Admitted {
		if !seen[id] {
			add(id, ShadowQueueMissing, "admitted", "absent")
		}
	}
	return s
}

// shadowReleased classes a thing the old path held and the queue works.
func shadowReleased(reason string) ShadowClass {
	switch {
	case reason == "outside_home":
		return ShadowHomeSplit
	case strings.HasPrefix(reason, "demand:"):
		return ShadowDemandGone
	case !strings.Contains(reason, ":"):
		return ShadowAdmitsHeld
	}
	return ShadowReachGone
}
