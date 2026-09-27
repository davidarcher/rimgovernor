package bridge

import (
	"context"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const mirrorPollMethod = "rimgovernor/mirror_poll"

// MirrorPollMaxBytes is the most section bytes one mirror page may carry;
// the rest of the one MiB envelope is the journal page's.
const MirrorPollMaxBytes = 768 * 1024

// MirrorJournalLimit is the events page size a mirror poll's journal page
// is read with.
const MirrorJournalLimit = 128

// WritesPending reports whether a typed side-effect call is queued or in
// flight on this client: the mirror poll loop then polls without waiting,
// so a command never waits behind an idle poll on a transport that
// serializes calls (#795).
func (client *Client) WritesPending() bool { return client.writes.Load() > 0 }

// MirrorPoll reads one page of the colony mirror (#795): per asked section
// a keyframe or the rows changed after its watermark, and the clock
// journal page after the request's cursor, from one native snapshot. The
// call is admitted as the mirror class unless ctx overrides it (a review's
// own immediate poll is an observation read).
func (client *Client) MirrorPoll(ctx context.Context, request *mp.MirrorPollRequest) (*mp.MirrorPollReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("mirror poll request required")
	}
	if err := validateMirrorPollRequest(request); err != nil {
		return nil, Result{}, err
	}
	reply := &mp.MirrorPollReply{}
	raw, err := client.protoRead(ctx, mirrorPollMethod, request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *mp.MirrorPollReply_Failure:
		err = failure(v.Failure, raw)
	case *mp.MirrorPollReply_Page:
		err = ValidateMirrorPage(v.Page, request)
	default:
		err = contract("mirror poll outcome missing")
	}
	return reply, raw, err
}

func validateMirrorPollRequest(request *mp.MirrorPollRequest) error {
	if request.Identity != nil {
		if err := ValidateIdentity(request.Identity); err != nil {
			return err
		}
	}
	if request.GetByteBudget() < 1 || request.GetByteBudget() > MirrorPollMaxBytes || request.GetWaitMs() > ClockEventsMaxWaitMs || request.JournalAfterCursor != nil && request.GetJournalAfterCursor() < 0 {
		return contract("mirror poll budget, wait or cursor bounds")
	}
	seen := map[mp.Section]bool{}
	for _, ask := range request.Asks {
		if ask.GetSection() == mp.Section_SECTION_UNSPECIFIED || seen[ask.GetSection()] {
			return contract("mirror poll sections must be distinct and known")
		}
		if (ask.GetSection() == mp.Section_SECTION_PLANNING_CELLS) != (ask.Window != nil) || ask.Window != nil && !validGridRect(ask.Window) {
			return contract("mirror poll window on exactly the planning cells ask")
		}
		seen[ask.GetSection()] = true
	}
	return nil
}

// MirrorJournalRequest is the events request a mirror page's journal
// answers, for the clock journal's own validation and persistence.
func MirrorJournalRequest(identity *c.Identity, request *mp.MirrorPollRequest) *k.EventsRequest {
	return &k.EventsRequest{Identity: proto.Clone(identity).(*c.Identity), AfterCursor: proto.Int64(request.GetJournalAfterCursor()), Limit: proto.Uint32(MirrorJournalLimit)}
}

// ValidateMirrorPage checks a page against the poll that asked for it: a
// complete epoch, only asked sections, each once, a body or more, rows with
// ids, a delta that starts at the ask's watermark under the same epoch, and
// a journal page exactly when one was asked.
func ValidateMirrorPage(page *mp.MirrorPage, request *mp.MirrorPollRequest) error {
	if page == nil || page.Epoch == nil || page.Epoch.GetProcess() == "" || page.CompleteThroughTick == nil {
		return contract("mirror page epoch or completeness missing")
	}
	if err := ValidateIdentity(page.Epoch.Identity); err != nil {
		return err
	}
	if request.Identity != nil && !sameIdentity(page.Epoch.Identity, request.Identity) {
		return contract("mirror page identity mismatch")
	}
	asks := map[mp.Section]*mp.SectionAsk{}
	for _, ask := range request.Asks {
		asks[ask.GetSection()] = ask
	}
	same := proto.Equal(page.Epoch, request.Epoch)
	served := map[mp.Section]bool{}
	for _, section := range page.Sections {
		ask, ok := asks[section.GetSection()]
		if !ok || served[section.GetSection()] {
			return contract("mirror page section not asked or repeated")
		}
		served[section.GetSection()] = true
		if err := validateSectionPage(section, ask, same); err != nil {
			return err
		}
	}
	if (request.JournalAfterCursor != nil) != (page.Journal != nil) {
		return contract("mirror page journal presence")
	}
	if page.Journal != nil {
		journal := MirrorJournalRequest(page.Epoch.Identity, request)
		if err := clockEventsPage(page.Journal, journal); err != nil {
			return err
		}
		if page.Journal.Context.GetTick() < page.GetCompleteThroughTick() {
			return contract("mirror page complete past its journal")
		}
	}
	return nil
}

func validateSectionPage(section *mp.SectionPage, ask *mp.SectionAsk, same bool) error {
	switch body := section.Body.(type) {
	case nil:
		if !section.GetMore() {
			return contract("mirror section without body or more")
		}
		return nil
	case *mp.SectionPage_Keyframe:
		if body.Keyframe.At == nil {
			return contract("mirror keyframe without watermark")
		}
		return mirrorRows(section.GetSection(), ask, body.Keyframe.Buildings, body.Keyframe.Bills, body.Keyframe.Cells)
	case *mp.SectionPage_Delta:
		d := body.Delta
		if !same || d.From == nil || d.To == nil || !proto.Equal(d.From, ask.GetSince()) || !MirrorBefore(d.From, d.To) {
			return contract("mirror delta watermarks")
		}
		for _, id := range d.Tombstones {
			if id == "" {
				return contract("mirror tombstone without id")
			}
		}
		if section.GetSection() == mp.Section_SECTION_PLANNING_CELLS && len(d.Tombstones) > 0 {
			return contract("mirror cell grid delta with tombstones")
		}
		return mirrorRows(section.GetSection(), ask, d.Buildings, d.Bills, d.Cells)
	}
	return contract("mirror section body")
}

func mirrorRows(section mp.Section, ask *mp.SectionAsk, buildings []*o.BuildingState, bills []*o.BillStack, cells *mp.CellGrid) error {
	if (section == mp.Section_SECTION_PLANNING_CELLS) != (cells != nil) {
		return contract("mirror cell grid on exactly the planning cells section")
	}
	switch section {
	case mp.Section_SECTION_PLANNING_CELLS:
		if len(buildings)+len(bills) > 0 || !proto.Equal(cells.Rect, ask.GetWindow()) {
			return contract("mirror cell grid off the asked window")
		}
	case mp.Section_SECTION_BUILDINGS:
		if len(bills) > 0 {
			return contract("mirror buildings section carries bills")
		}
		for _, row := range buildings {
			if row.GetBuilding().GetId() == "" {
				return contract("mirror building row without id")
			}
		}
	case mp.Section_SECTION_BILLS:
		if len(buildings) > 0 {
			return contract("mirror bills section carries buildings")
		}
		for _, row := range bills {
			if row.GetBench().GetId() == "" {
				return contract("mirror bill stack without bench id")
			}
		}
	default:
		return contract("mirror section unknown")
	}
	return nil
}

// MirrorBefore orders watermarks by tick, then seq.
func MirrorBefore(a, b *mp.Watermark) bool {
	return a.GetTick() < b.GetTick() || a.GetTick() == b.GetTick() && a.GetSeq() < b.GetSeq()
}
