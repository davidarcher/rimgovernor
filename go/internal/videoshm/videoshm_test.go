package videoshm

import (
	"bytes"
	"encoding/binary"
	"math"
	"testing"
)

func header(sequence uint64, width, height int32, captured, readback float64, selection int32) []byte {
	var buf bytes.Buffer
	_ = binary.Write(&buf, binary.LittleEndian, sequence)
	_ = binary.Write(&buf, binary.LittleEndian, width)
	_ = binary.Write(&buf, binary.LittleEndian, height)
	_ = binary.Write(&buf, binary.LittleEndian, captured)
	_ = binary.Write(&buf, binary.LittleEndian, readback)
	_ = binary.Write(&buf, binary.LittleEndian, selection)
	_ = binary.Write(&buf, binary.LittleEndian, int32(0))
	return buf.Bytes()
}

func TestParseFrameCopiesPixelsPastPrevious(t *testing.T) {
	pixels := []byte{1, 2, 3, 4, 5, 6, 7, 8}
	buf := append(header(7, 2, 1, 1700000000.5, 2.25, 42), pixels...)
	frame, ok, err := parseFrame(buf, 6)
	if err != nil || !ok {
		t.Fatal(ok, err)
	}
	if frame.Sequence != 7 || frame.Width != 2 || frame.Height != 1 || frame.CapturedUnixMs != 1700000000500 || frame.ReadbackMs != 2.25 || frame.Selection != 42 {
		t.Fatalf("%+v", frame)
	}
	if !bytes.Equal(frame.Data, pixels) {
		t.Fatal(frame.Data)
	}
	buf[headerSize] = 99
	if frame.Data[0] != 1 {
		t.Fatal("frame data must be a copy, not a view of the buffer")
	}
	if _, ok, err := parseFrame(buf, 7); ok || err != nil {
		t.Fatal("same sequence must not be a new frame", ok, err)
	}
}

func TestParseFrameRejectsBadHeaders(t *testing.T) {
	if _, ok, err := parseFrame(header(0, 2, 1, 0, 0, 0), 0); ok || err != nil {
		t.Fatal("sequence zero means no frame yet", ok, err)
	}
	if _, _, err := parseFrame(header(1, 0, 1, 0, 0, 0), 0); err == nil {
		t.Fatal("zero width must fail")
	}
	if _, _, err := parseFrame(header(1, MaxWidth+1, 1, 0, 0, 0), 0); err == nil {
		t.Fatal("oversize width must fail")
	}
	if _, _, err := parseFrame(header(1, 2, 1, 0, 0, 0), 0); err == nil {
		t.Fatal("short pixel body must fail")
	}
	if _, _, err := parseFrame(append(header(1, 1, 1, math.NaN(), 0, 0), 0, 0, 0, 0), 0); err == nil {
		t.Fatal("NaN timestamp must fail")
	}
	if _, _, err := parseFrame(make([]byte, headerSize-1), 0); err == nil {
		t.Fatal("short header must fail")
	}
}

type memoryMapping struct {
	buf    []byte
	busy   bool
	closed bool
}

func (m *memoryMapping) bytes() []byte { return m.buf }
func (m *memoryMapping) lock() bool    { return !m.busy }
func (m *memoryMapping) unlock()       {}
func (m *memoryMapping) close() error  { m.closed = true; return nil }

func TestReaderSkipsWhenLockIsBusy(t *testing.T) {
	m := &memoryMapping{buf: append(header(3, 1, 1, 1, 1, 0), 9, 9, 9, 9)}
	r := &reader{m: m}
	if _, ok, err := r.Read(0); !ok || err != nil {
		t.Fatal(ok, err)
	}
	m.busy = true
	if _, ok, err := r.Read(0); ok || err != nil {
		t.Fatal("a busy producer lock must yield no frame, not an error", ok, err)
	}
	if err := r.Close(); err != nil || !m.closed {
		t.Fatal(err, m.closed)
	}
}
