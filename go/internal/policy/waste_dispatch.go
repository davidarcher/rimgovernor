package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// WasteHaulerUnavailable mirrors CleanerUnavailable: the pawn is dead, downed,
// occupied by a conflicting player order, or already running this job.
const WasteHaulerUnavailable Reason = "waste_hauler_unavailable"

// WasteItemIneligible mirrors FilthIneligible: the item this action exists to
// haul or bury has already been removed or resolved (by this pawn, another
// pawn, or the player), or native no longer judges it eligible.
const WasteItemIneligible Reason = "waste_item_ineligible"

// WastePawnFacts describes the one already-selected undrafted candidate pawn.
// The planner has already chosen this pawn and item; EvaluateWaste only
// re-validates the pair immediately before dispatch.
type WastePawnFacts struct {
	Pawn                               domain.PawnID
	SnapshotToken                      string
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	ExistingJobDef                     domain.Fact[string]
}

// WasteItemDispatchFacts describes the one already-selected waste item.
// Exists distinguishes an item already removed or resolved (by any means,
// including native relocating or burying it itself) from an incomplete cell
// scan; unlike a structure's hit points, a waste item has no partial-progress
// fact to re-check, only presence. Native eligibility is re-checked through
// NativeCanTry (the preview's own accept signal), not here, mirroring
// CleanFilthFacts.
type WasteItemDispatchFacts struct {
	Item          string
	SnapshotToken string
	Exists        domain.Fact[bool]
}

type WasteDispatchFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  WastePawnFacts
	Item                  WasteItemDispatchFacts
	NativeCanTry          domain.Fact[bool]
}

type WasteDispatchRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       WasteDispatchFacts
}

// EvaluateWaste re-validates one already-selected pawn/item pair immediately
// before dispatch. Admission proves eligibility now; it does not prove the
// haul or burial job will be issued, accepted or completed. This has no
// analogue to Repair's partial hit-point progress: a waste item is either
// present and eligible or it is not.
func EvaluateWaste(r WasteDispatchRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	waste, ok := r.Action.Waste()
	canonical, err := domain.NewWasteAction(r.Action.ID(), waste)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	// The admission anchors on the preview tick, the inspection's one live
	// read; the pawn row may come from the step's fact cache up to the
	// planning tolerance behind it under a running window (#306, #323).
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || !f.PreviewTick.FreshFor(f.PawnTick) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != waste.Pawn() || f.Item.Item != waste.Target() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.Item.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.MentalState} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	if _, known := f.Pawn.ExistingJobDef.Value(); !known {
		return refuse(UnknownFacts)
	}
	existingJob, _ := f.Pawn.ExistingJobDef.Value()
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.MentalState.Value()
	if dead || downed {
		return refuse(WasteHaulerUnavailable)
	}
	if drafted || mental {
		return refuse(PlayerOrder)
	}
	if existingJob == "HaulToCell" || existingJob == "HaulToContainer" {
		return refuse(WasteHaulerUnavailable)
	}
	exists, known := f.Item.Exists.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !exists {
		return refuse(WasteItemIneligible)
	}
	nativeCanTry, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !nativeCanTry {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
