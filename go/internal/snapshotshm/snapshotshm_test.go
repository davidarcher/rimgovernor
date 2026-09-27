package snapshotshm

import (
	"context"
	"encoding/binary"
	"sync/atomic"
	"testing"
	"time"
	"unsafe"
)

// memMapping is a ring in Go memory written the way the native stream
// writes it.
type memMapping struct {
	buf              []byte
	slots, slotBytes uint64
}

func newMem(slots, slotBytes uint64) *memMapping {
	// uint64 backing keeps the atomic offsets aligned.
	words := make([]uint64, (headerBytes+slots*slotBytes)/8)
	buf := unsafe.Slice((*byte)(unsafe.Pointer(&words[0])), len(words)*8)
	binary.LittleEndian.PutUint32(buf[0:], magic)
	binary.LittleEndian.PutUint32(buf[4:], version)
	binary.LittleEndian.PutUint32(buf[8:], uint32(slots))
	binary.LittleEndian.PutUint32(buf[12:], uint32(slotBytes))
	return &memMapping{buf: buf, slots: slots, slotBytes: slotBytes}
}

func (m *memMapping) store(offset, value uint64) {
	atomic.StoreUint64((*uint64)(unsafe.Pointer(&m.buf[offset])), value)
}

// publish writes frame n; torn leaves the slot mid-write.
func (m *memMapping) publish(n uint64, writes int64, payload []byte, torn bool) {
	slot := headerBytes + (n%m.slots)*m.slotBytes
	m.store(slot, 2*n+1)
	binary.LittleEndian.PutUint64(m.buf[slot+8:], uint64(writes))
	binary.LittleEndian.PutUint32(m.buf[slot+16:], uint32(len(payload)))
	binary.LittleEndian.PutUint32(m.buf[slot+20:], uint32(n)*10)
	binary.LittleEndian.PutUint32(m.buf[slot+24:], uint32(n)*20)
	binary.LittleEndian.PutUint32(m.buf[slot+28:], uint32(n)*30)
	copy(m.buf[slot+slotHeaderBytes:], payload)
	if torn {
		return
	}
	m.store(slot, 2*n)
	m.store(16, n)
}

func (m *memMapping) bytes() []byte           { return m.buf }
func (m *memMapping) wait(time.Duration) bool { return false }
func (m *memMapping) close() error            { return nil }

func TestLatestReadsTheCommittedHead(t *testing.T) {
	m := newMem(3, 64)
	r, err := newReader(m)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok, err := r.Latest(); ok || err != nil {
		t.Fatalf("empty ring: ok=%v err=%v", ok, err)
	}
	m.publish(1, 0, []byte("one"), false)
	m.publish(2, 5, []byte("two"), false)
	frame, ok, err := r.Latest()
	if err != nil || !ok || frame.Number != 2 || frame.Writes != 5 || string(frame.Payload) != "two" || frame.CaptureMicros != 20 || frame.EncodeMicros != 40 || frame.WriteMicros != 60 {
		t.Fatalf("latest = %+v ok=%v err=%v", frame, ok, err)
	}
	// Frame 3 mid-write does not move the head; a lapped slot is refused.
	m.publish(3, 6, []byte("three"), true)
	if frame, ok, _ := r.Latest(); !ok || frame.Number != 2 {
		t.Fatalf("torn write: %+v ok=%v", frame, ok)
	}
	if _, ok, _ := r.read(3); ok {
		t.Fatal("a slot mid-write read as committed")
	}
	m.publish(5, 7, []byte("five"), false) // overwrites frame 2's slot
	if _, ok, _ := r.read(2); ok {
		t.Fatal("a lapped frame read as current")
	}
}

func TestWaitSeesALaterCommit(t *testing.T) {
	m := newMem(2, 64)
	r, err := newReader(m)
	if err != nil {
		t.Fatal(err)
	}
	if r.Wait(context.Background(), 0, 10*time.Millisecond) {
		t.Fatal("wait returned without a frame")
	}
	go func() {
		time.Sleep(5 * time.Millisecond)
		m.publish(1, 0, []byte("x"), false)
	}()
	if !r.Wait(context.Background(), 0, 5*time.Second) {
		t.Fatal("wait missed the commit")
	}
}

func TestNewReaderRefusesAForeignRing(t *testing.T) {
	m := newMem(2, 64)
	binary.LittleEndian.PutUint32(m.buf[0:], 1)
	if _, err := newReader(m); err == nil {
		t.Fatal("foreign magic accepted")
	}
}
