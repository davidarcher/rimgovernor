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

const (
	colonyFactsMethod = "rimgovernor/observations_read_colony_facts"
	listPawnsMethod   = "rimgovernor/observations_list_pawns"
)

// routinePawnDetails is the pawn detail ReadRoutinePawns reads.
var routinePawnDetails = pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true}

// WritesPending reports whether a typed side-effect call is queued or in
// flight on this client: the mirror poll loop then polls without waiting,
// so a command never waits behind an idle poll on a transport that
// serializes calls (#795).
func (client *Client) WritesPending() bool { return client.writes.Load() > 0 }

// MirrorColonyFactsAsk is the colony facts section ask: the planning
// colony facts read (ReadColonyFacts with planning, no definitions).
func MirrorColonyFactsAsk(identity *c.Identity) *mp.SectionAsk {
	return &mp.SectionAsk{Section: mp.Section_SECTION_COLONY_FACTS.Enum(), ColonyFacts: colonyFactsRequest(identity, true, nil)}
}

// MirrorPawnsAsk is the pawns section ask: ReadRoutinePawns of ids.
func MirrorPawnsAsk(identity *c.Identity, ids []string) *mp.SectionAsk {
	return &mp.SectionAsk{Section: mp.Section_SECTION_PAWNS.Enum(), Pawns: pawnDetailsRequest(identity, ids, routinePawnDetails)}
}

// RoutinePawnIDs are the colonists a bundle's emergency census lists
// completely, the roster ReadRoutinePawns reads; nil when it does not.
func RoutinePawnIDs(v *o.BundleSnapshot) []string {
	emergency, err := BundleEmergency(v)
	if err != nil {
		return nil
	}
	return routinePawnIDs(emergency)
}

// mirrorTracked is a pawns or colony facts ask's #773 bookkeeping: the
// dedicated read's method and delta store key, and the ask it was sent.
type mirrorTracked struct {
	method string
	key    string
	read   proto.Message // the ask's request without changed_since
	ask    *o.SectionDeltaAsk
}

// MirrorPoll reads one page of the colony mirror (#795): per asked section
// a keyframe or the rows changed after its watermark, and the clock
// journal page after the request's cursor, from one native snapshot. The
// call is admitted as the mirror class unless ctx overrides it (a review's
// own immediate poll is an observation read).
//
// The pawns and colony facts sections share the #773 delta store with
// their dedicated reads: the ask is sent with the store's changed_since,
// and the page's snapshot is completed from the held reply before the
// caller sees it, then seeded into the step's read cache under the
// dedicated read's request, so the review's own read of it is a hit. A
// snapshot that cannot be completed comes back as more (not served).
func (client *Client) MirrorPoll(ctx context.Context, request *mp.MirrorPollRequest) (*mp.MirrorPollReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("mirror poll request required")
	}
	if err := validateMirrorPollRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*mp.MirrorPollRequest)
	tracked := map[mp.Section]mirrorTracked{}
	for _, ask := range request.Asks {
		var t mirrorTracked
		switch {
		case ask.Pawns != nil:
			t = mirrorTracked{method: listPawnsMethod, read: proto.Clone(ask.Pawns)}
			t.key = deltaKey(t.method, t.read)
			t.ask = client.deltas.ask(t.key)
			ask.Pawns.ChangedSince = t.ask
		case ask.ColonyFacts != nil:
			t = mirrorTracked{method: colonyFactsMethod, read: proto.Clone(ask.ColonyFacts)}
			t.key = deltaKey(t.method, t.read)
			t.ask = client.deltas.ask(t.key)
			ask.ColonyFacts.ChangedSince = t.ask
		default:
			continue
		}
		tracked[ask.GetSection()] = t
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
		if err = ValidateMirrorPage(v.Page, request); err == nil {
			err = client.completeMirrorSnapshots(ctx, v.Page, tracked, raw)
		}
	default:
		err = contract("mirror poll outcome missing")
	}
	return reply, raw, err
}

// completeMirrorSnapshots completes, validates and seeds the page's pawns
// and colony facts snapshots.
func (client *Client) completeMirrorSnapshots(ctx context.Context, page *mp.MirrorPage, tracked map[mp.Section]mirrorTracked, raw Result) error {
	for _, section := range page.Sections {
		t, ok := tracked[section.GetSection()]
		keyframe := section.GetKeyframe()
		if !ok || keyframe == nil {
			continue
		}
		var snapshot proto.Message
		var delta *o.SectionDelta
		if keyframe.Pawns != nil {
			snapshot, delta = keyframe.Pawns, keyframe.Pawns.Delta
		} else {
			snapshot, delta = keyframe.ColonyFacts, keyframe.ColonyFacts.Delta
		}
		complete, err := client.completeDelta(ctx, t.method, t.key, t.ask, snapshot.ProtoReflect(), delta)
		if err != nil {
			return err
		}
		if !complete {
			section.Body, section.More = nil, proto.Bool(true)
			continue
		}
		var read proto.Message
		switch r := t.read.(type) {
		case *o.ListPawnsRequest:
			requested := make(map[string]bool, len(r.GetFilter().GetIds()))
			for _, id := range r.GetFilter().GetIds() {
				requested[id] = true
			}
			if err := pawnsSnapshotSelected(keyframe.Pawns, page.Epoch.Identity, requested, routinePawnDetails); err != nil {
				return err
			}
			read = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: keyframe.Pawns}}
		case *o.ColonyFactsRequest:
			if err := ValidateColonyFacts(keyframe.ColonyFacts, page.Epoch.Identity); err != nil {
				return err
			}
			read = &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: keyframe.ColonyFacts}}
		}
		seedRead(ctx, t.method, t.read, read, raw)
	}
	return nil
}

// seedRead stores reply in ctx's step read cache as the answer to method
// and request.
func seedRead(ctx context.Context, method string, request, reply proto.Message, raw Result) {
	cache := StepReadCacheFrom(ctx)
	if cache == nil {
		return
	}
	scope, ok := replyScope(reply)
	if !ok {
		return
	}
	encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
	if err != nil {
		return
	}
	payload, err := proto.Marshal(reply)
	if err != nil {
		return
	}
	cache.seed(readCacheKey{method: method, request: string(encoded)}, scope, payload, raw)
}

func validateMirrorPollRequest(request *mp.MirrorPollRequest) error {
	if request.Identity == nil {
		return contract("mirror poll identity required")
	}
	if err := ValidateIdentity(request.Identity); err != nil {
		return err
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
		if (ask.GetSection() == mp.Section_SECTION_PAWNS) != (ask.Pawns != nil) || (ask.GetSection() == mp.Section_SECTION_COLONY_FACTS) != (ask.ColonyFacts != nil) {
			return contract("mirror poll read on exactly the pawns and colony facts asks")
		}
		// Only the reads the review serves: the routine pawn detail of a
		// valid roster and the planning colony facts, for the poll's world.
		if ask.Pawns != nil {
			ids := ask.Pawns.GetFilter().GetIds()
			if len(ids) < 1 || len(ids) > 256 || !proto.Equal(ask.Pawns, pawnDetailsRequest(request.Identity, ids, routinePawnDetails)) {
				return contract("mirror poll pawns ask is not the routine pawn read")
			}
			for _, id := range ids {
				if err := validID(id); err != nil {
					return err
				}
			}
		}
		if ask.ColonyFacts != nil && !proto.Equal(ask.ColonyFacts, colonyFactsRequest(request.Identity, true, nil)) {
			return contract("mirror poll colony facts ask is not the planning read")
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
// complete epoch of the asked identity, only asked sections, each once, a
// body or more, rows with ids, a delta that starts at the ask's watermark
// under the same epoch, a resync keyframe only beside a delta that asked
// for one, and a journal page exactly when one was asked.
func ValidateMirrorPage(page *mp.MirrorPage, request *mp.MirrorPollRequest) error {
	if page == nil || page.Epoch == nil || page.Epoch.GetProcess() == "" || page.CompleteThroughTick == nil {
		return contract("mirror page epoch or completeness missing")
	}
	if err := ValidateIdentity(page.Epoch.Identity); err != nil {
		return err
	}
	if !sameIdentity(page.Epoch.Identity, request.Identity) {
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
		if err := validateSectionPage(section, ask, same, page.Epoch.Identity); err != nil {
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

func validateSectionPage(section *mp.SectionPage, ask *mp.SectionAsk, same bool, identity *c.Identity) error {
	tracked := section.GetSection() == mp.Section_SECTION_PAWNS || section.GetSection() == mp.Section_SECTION_COLONY_FACTS
	if section.Resync != nil && (section.GetDelta() == nil || !ask.GetResync()) {
		return contract("mirror resync keyframe without its delta")
	}
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
		return mirrorRows(section.GetSection(), ask, keyframeRows(body.Keyframe), identity)
	case *mp.SectionPage_Delta:
		d := body.Delta
		if tracked {
			return contract("mirror delta of a #773 section")
		}
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
		if section.Resync != nil {
			if !proto.Equal(section.Resync.At, d.To) {
				return contract("mirror resync keyframe off its delta's watermark")
			}
			if err := mirrorRows(section.GetSection(), ask, keyframeRows(section.Resync), identity); err != nil {
				return err
			}
		}
		return mirrorRows(section.GetSection(), ask, mirrorBody{buildings: d.Buildings, bills: d.Bills, cells: d.Cells, zones: d.Zones}, identity)
	}
	return contract("mirror section body")
}

// mirrorBody is what one section body carries.
type mirrorBody struct {
	buildings []*o.BuildingState
	bills     []*o.BillStack
	cells     *mp.CellGrid
	zones     *o.ZonesSnapshot
	pawns     *o.PawnSnapshot
	colony    *o.ColonyFactsSnapshot
}

func keyframeRows(k *mp.Keyframe) mirrorBody {
	return mirrorBody{buildings: k.Buildings, bills: k.Bills, cells: k.Cells, zones: k.Zones, pawns: k.Pawns, colony: k.ColonyFacts}
}

// mirrorRows checks a body carries exactly its section's rows.
func mirrorRows(section mp.Section, ask *mp.SectionAsk, b mirrorBody, identity *c.Identity) error {
	carried := map[mp.Section]bool{
		mp.Section_SECTION_BUILDINGS:      len(b.buildings) > 0,
		mp.Section_SECTION_BILLS:          len(b.bills) > 0,
		mp.Section_SECTION_PLANNING_CELLS: b.cells != nil,
		mp.Section_SECTION_ZONES:          b.zones != nil,
		mp.Section_SECTION_PAWNS:          b.pawns != nil,
		mp.Section_SECTION_COLONY_FACTS:   b.colony != nil,
	}
	for other, has := range carried {
		if has && other != section {
			return contract("mirror %s section carries %s rows", section, other)
		}
	}
	switch section {
	case mp.Section_SECTION_PLANNING_CELLS:
		if b.cells == nil || !proto.Equal(b.cells.Rect, ask.GetWindow()) {
			return contract("mirror cell grid off the asked window")
		}
	case mp.Section_SECTION_BUILDINGS:
		for _, row := range b.buildings {
			if row.GetBuilding().GetId() == "" {
				return contract("mirror building row without id")
			}
		}
	case mp.Section_SECTION_BILLS:
		for _, row := range b.bills {
			if row.GetBench().GetId() == "" {
				return contract("mirror bill stack without bench id")
			}
		}
	case mp.Section_SECTION_ZONES:
		if b.zones == nil || len(b.zones.RemovedIds) > 0 {
			return contract("mirror zones census missing or with removed ids")
		}
		if err := ValidateContext(b.zones.Context); err != nil {
			return err
		}
		if !sameIdentity(b.zones.Context.GetIdentity(), identity) {
			return contract("mirror zones world mismatch")
		}
		for _, row := range b.zones.Zones {
			if row.GetId() == "" {
				return contract("mirror zone row without id")
			}
		}
	case mp.Section_SECTION_PAWNS:
		if b.pawns == nil {
			return contract("mirror pawns snapshot missing")
		}
	case mp.Section_SECTION_COLONY_FACTS:
		if b.colony == nil {
			return contract("mirror colony facts snapshot missing")
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
