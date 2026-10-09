// Package snapshotshm reads the snapshot stream: the ring of
// BundleSnapshot frames the native SnapshotStream publishes
// (integrations/rimgovernor-native/src/Bridge/Protocol/SnapshotStream.cs)
// into named shared memory. Reads never lock: each slot is a seqlock, so a
// reader copies a frame and keeps it only when the slot's sequence did not
// move while it copied. It only works when the controller shares a host with
// the game; Open reports ErrUnavailable otherwise.
//
// Layout (little-endian):
//
//	header, 64 bytes:
//	  0  uint32 magic "RGSS"   4  uint32 version
//	  8  uint32 slots          12 uint32 slot bytes
//	  16 uint64 head: the last committed frame number (0: none)
//	  24 int64  writes: the native applied-write counter
//	slot i at 64 + i*slotBytes; frame n lives in slot n % slots:
//	  0  uint64 seqlock: 2n+1 while writing, 2n once committed
//	  8  int64  writes the frame was captured at
//	  16 uint32 payload length
//	  20 uint32 capture microseconds (native game thread)
//	  24 uint32 encode microseconds  28 uint32 write microseconds
//	  40 payload
package snapshotshm

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"sync/atomic"
	"time"
	"unsafe"
)

const (
	magic   = 0x53534752
	version = 2
	// The reply ring shares the slot layout under its own magic
	// "RGRR"; its slots hold raw contract replies and nothing reads its head.
	replyMagic      = 0x52524752
	replyVersion    = 1
	headerBytes     = 64
	slotHeaderBytes = 40
	// pollInterval paces Wait where the platform has no ready event, and
	// after a spurious wake.
	pollInterval = 2 * time.Millisecond
)

// ErrUnavailable means no ring with that name can be opened here.
var ErrUnavailable = errors.New("snapshot shared memory unavailable")

// Frame is one committed frame copied out of the ring.
type Frame struct {
	Number  uint64
	Writes  int64 // the native write counter when the frame was captured
	Payload []byte
	// The native cost of the frame, in microseconds: capturing it on the
	// game thread, encoding it, and writing it into its slot.
	CaptureMicros, EncodeMicros, WriteMicros uint32
}

// mapping is the platform view of the ring plus its ready signal.
type mapping interface {
	bytes() []byte
	// wait blocks up to d for the ready event; false when the platform has
	// none, so the caller polls.
	wait(d time.Duration) bool
	close() error
}

// Reader reads frames from one named ring.
type Reader struct {
	m                mapping
	slots, slotBytes uint64
}

func newReader(m mapping) (*Reader, error) { return newRingReader(m, magic, version) }

func newRingReader(m mapping, wantMagic, wantVersion uint32) (*Reader, error) {
	buf := m.bytes()
	if len(buf) < headerBytes {
		_ = m.close()
		return nil, fmt.Errorf("%w: ring below its header", ErrUnavailable)
	}
	if binary.LittleEndian.Uint32(buf[0:4]) != wantMagic || binary.LittleEndian.Uint32(buf[4:8]) != wantVersion {
		_ = m.close()
		return nil, fmt.Errorf("%w: ring magic or version differs", ErrUnavailable)
	}
	r := &Reader{m: m, slots: uint64(binary.LittleEndian.Uint32(buf[8:12])), slotBytes: uint64(binary.LittleEndian.Uint32(buf[12:16]))}
	if r.slots < 2 || r.slotBytes <= slotHeaderBytes || uint64(len(buf)) < headerBytes+r.slots*r.slotBytes {
		_ = m.close()
		return nil, fmt.Errorf("%w: ring geometry %dx%d does not fit %d bytes", ErrUnavailable, r.slots, r.slotBytes, len(buf))
	}
	return r, nil
}

// load reads an aligned uint64 the producer writes, atomically.
func (r *Reader) load(offset uint64) uint64 {
	return atomic.LoadUint64((*uint64)(unsafe.Pointer(&r.m.bytes()[offset])))
}

// Open maps the named snapshot ring and its ready event.
func Open(name string) (*Reader, error) {
	m, err := openMapping(name, true)
	if err != nil {
		return nil, err
	}
	return newReader(m)
}

// OpenReplies maps the named native reply ring: large contract
// replies the native side wrote into a slot instead of the GABP reply.
func OpenReplies(name string) (*Reader, error) {
	m, err := openMapping(name, false)
	if err != nil {
		return nil, err
	}
	return newRingReader(m, replyMagic, replyVersion)
}

// Slots is the ring's slot count.
func (r *Reader) Slots() uint64 { return r.slots }

// Read copies entry n; ok is false when its slot no longer holds n (it was
// overwritten, or is being written).
func (r *Reader) Read(n uint64) (Frame, bool, error) { return r.read(n) }

// Head is the last committed frame number, 0 before the first.
func (r *Reader) Head() uint64 { return r.load(16) }

// Writes is the native applied-write counter now: a frame captured at or
// past it reflects every write that returned before this call.
func (r *Reader) Writes() int64 { return int64(r.load(24)) }

// Latest copies the newest committed frame; ok is false before the first
// frame. A frame overwritten while it was copied is retried from the new
// head.
func (r *Reader) Latest() (Frame, bool, error) {
	for attempt := 0; attempt < 8; attempt++ {
		n := r.Head()
		if n == 0 {
			return Frame{}, false, nil
		}
		frame, ok, err := r.read(n)
		if err != nil || ok {
			return frame, ok, err
		}
	}
	return Frame{}, false, nil
}

// read copies frame n, or reports false when its slot moved under the copy.
func (r *Reader) read(n uint64) (Frame, bool, error) {
	slot := headerBytes + (n%r.slots)*r.slotBytes
	before := r.load(slot)
	if before != 2*n {
		return Frame{}, false, nil
	}
	buf := r.m.bytes()
	writes := int64(binary.LittleEndian.Uint64(buf[slot+8 : slot+16]))
	length := uint64(binary.LittleEndian.Uint32(buf[slot+16 : slot+20]))
	if length > r.slotBytes-slotHeaderBytes {
		// A torn header: the recheck below decides.
		if r.load(slot) != before {
			return Frame{}, false, nil
		}
		return Frame{}, false, fmt.Errorf("snapshot frame %d claims %d bytes, over its %d-byte slot", n, length, r.slotBytes-slotHeaderBytes)
	}
	capture := binary.LittleEndian.Uint32(buf[slot+20 : slot+24])
	encode := binary.LittleEndian.Uint32(buf[slot+24 : slot+28])
	write := binary.LittleEndian.Uint32(buf[slot+28 : slot+32])
	payload := make([]byte, length)
	copy(payload, buf[slot+slotHeaderBytes:slot+slotHeaderBytes+length])
	if r.load(slot) != before {
		return Frame{}, false, nil
	}
	return Frame{Number: n, Writes: writes, Payload: payload, CaptureMicros: capture, EncodeMicros: encode, WriteMicros: write}, true, nil
}

// Wait blocks until a frame past after is committed, ctx ends or timeout
// passes; it reports whether one was.
func (r *Reader) Wait(ctx context.Context, after uint64, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for {
		if r.Head() > after {
			return true
		}
		left := time.Until(deadline)
		if left <= 0 || ctx.Err() != nil {
			return false
		}
		step := min(left, 50*time.Millisecond)
		// The event stays set between a commit and the next write, so a
		// wake with no new head is followed by a short poll.
		if !r.m.wait(step) || r.Head() <= after {
			time.Sleep(min(pollInterval, max(left, 0)))
		}
	}
}

// Close unmaps the ring.
func (r *Reader) Close() error { return r.m.close() }
