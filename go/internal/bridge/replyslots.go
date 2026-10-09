package bridge

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
)

// Large replies through shared memory. A client of a game on this
// host asks for encoding=proto-shm: native writes a reply at or above its
// inline threshold, raw, into a slot of its reply ring and the wrapper
// carries only {"slot":{"ring","slot","seq","length"}}; smaller replies
// stay inline proto-gzip. The ring is fixed and sized well above the
// native encoder workers, so an entry overwritten before it is read is a
// contract error, never retried. A ring that cannot be opened here (a
// controller on another host) switches the session to proto-gzip.
const replyEncodingShm = "proto-shm"

// replySlotRef is the wrapper's "slot" value.
type replySlotRef struct {
	Ring   string `json:"ring"`
	Slot   uint64 `json:"slot"`
	Seq    uint64 `json:"seq"`
	Length int    `json:"length"`
}

// replySlotReader is the ring snapshotshm.Reader maps; tests substitute one.
type replySlotReader interface {
	Slots() uint64
	Read(n uint64) (snapshotshm.Frame, bool, error)
	Close() error
}

type replySlots struct {
	open func(name string) (replySlotReader, error)

	mu       sync.Mutex
	disabled bool
	name     string
	reader   replySlotReader
}

func newReplySlots() *replySlots {
	return &replySlots{open: func(name string) (replySlotReader, error) { return snapshotshm.OpenReplies(name) }}
}

// encoding is the reply encoding argument this client sends.
func (s *replySlots) encoding() string {
	if s == nil {
		return replyEncodingArgument
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.disabled {
		return replyEncodingArgument
	}
	return replyEncodingShm
}

// read copies the entry ref names. A ring that will not open disables
// proto-shm for the session and the call is unavailable; an entry no
// longer in its slot is a contract error.
func (s *replySlots) read(ref replySlotRef, limit int) ([]byte, error) {
	if ref.Ring == "" || ref.Seq == 0 || ref.Length < 0 || ref.Length > limit {
		return nil, contract("invalid reply slot")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.reader == nil || s.name != ref.Ring {
		// A restarted game names a new ring.
		reader, err := s.open(ref.Ring)
		if err != nil {
			s.disabled = true
			return nil, fmt.Errorf("%w: reply ring %s: %v; replies now inline", ErrUnavailable, ref.Ring, err)
		}
		if s.reader != nil {
			_ = s.reader.Close()
		}
		s.reader, s.name = reader, ref.Ring
	}
	if ref.Slot != ref.Seq%s.reader.Slots() {
		return nil, contract("reply slot %d does not hold seq %d", ref.Slot, ref.Seq)
	}
	entry, ok, err := s.reader.Read(ref.Seq)
	if err != nil {
		return nil, contract("reply slot: %v", err)
	}
	if !ok {
		return nil, contract("reply seq %d was overwritten before it was read", ref.Seq)
	}
	if len(entry.Payload) != ref.Length {
		return nil, contract("reply seq %d holds %d bytes, wrapper says %d", ref.Seq, len(entry.Payload), ref.Length)
	}
	return entry.Payload, nil
}

func (s *replySlots) close() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.reader != nil {
		_ = s.reader.Close()
		s.reader, s.name = nil, ""
	}
}

// resolveReplySlot reads the slot a raw receipt's wrapper names, before
// a later reply can overwrite it. data is nil for a receipt without one.
// When the receipt is kept (a transcript or flight recorder), the
// returned receipt carries the bytes inline as "proto_raw" (base64 of the
// raw proto) in place of "slot", so recordings decode without the ring.
func (s *replySlots) resolveReplySlot(raw json.RawMessage, keep bool) (json.RawMessage, []byte, error) {
	if s == nil || !bytes.Contains(raw, []byte(`"slot"`)) {
		return raw, nil, nil
	}
	var envelope map[string]json.RawMessage
	if json.Unmarshal(raw, &envelope) != nil {
		return raw, nil, nil
	}
	var structured map[string]json.RawMessage
	if json.Unmarshal(envelope["structuredContent"], &structured) != nil {
		return raw, nil, nil
	}
	slot, ok := structured["slot"]
	if !ok {
		return raw, nil, nil
	}
	var ref replySlotRef
	d := json.NewDecoder(bytes.NewReader(slot))
	d.DisallowUnknownFields()
	if d.Decode(&ref) != nil {
		return raw, nil, contract("invalid reply slot")
	}
	data, err := s.read(ref, maxReplyProtoBytes)
	if err != nil || !keep {
		return raw, data, err
	}
	delete(structured, "slot")
	structured["proto_raw"] = encode(base64.StdEncoding.EncodeToString(data))
	envelope["structuredContent"] = encode(structured)
	return encode(envelope), data, nil
}
