// Package videoshm reads the native video frame buffer VideoStreamDriver
// publishes (integrations/rimgovernor-native/src/Bridge/VideoStreamTool.cs)
// directly out of shared memory, bypassing the base64 ProtoJSON ReadFrame
// round trip. It only works when the controller shares a host with the game;
// Open reports ErrUnavailable otherwise and callers fall back to ReadFrame.
//
// Buffer layout (little-endian), written under the producer's lock:
//
//	0  int64   sequence (0 until the first frame)
//	8  int32   width
//	12 int32   height
//	16 float64 captured, Unix seconds
//	24 float64 readback, milliseconds
//	32 int32   selection hash
//	40 pixels  width*height*4 bytes
package videoshm

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"time"
)

const (
	MaxWidth   = 3840
	MaxHeight  = 2160
	headerSize = 40
	// Capacity mirrors the native buffer size exactly.
	Capacity = headerSize + MaxWidth*MaxHeight*4
	// lockWait bounds how long Read waits for the producer's lock; the
	// producer itself never blocks (it skips a frame when the reader holds it).
	lockWait = 50 * time.Millisecond
)

// ErrUnavailable means no buffer with that name can be opened here (other
// host, already released, or a platform without a shared-memory reader).
var ErrUnavailable = errors.New("video shared memory unavailable")

// Frame is one published frame copied out of the buffer.
type Frame struct {
	Sequence       uint64
	Width, Height  int
	CapturedUnixMs int64
	ReadbackMs     float64
	Selection      int32
	Data           []byte
}

// Reader reads frames from one named buffer.
type Reader interface {
	// Read returns the latest frame when its sequence is past previous; ok is
	// false when nothing newer has been published yet or the lock was busy.
	Read(previous uint64) (frame Frame, ok bool, err error)
	Close() error
}

// mapping is the platform-specific view of the buffer plus its lock.
type mapping interface {
	bytes() []byte
	lock() bool
	unlock()
	close() error
}

type reader struct{ m mapping }

func (r *reader) Read(previous uint64) (Frame, bool, error) {
	if !r.m.lock() {
		return Frame{}, false, nil
	}
	defer r.m.unlock()
	return parseFrame(r.m.bytes(), previous)
}

func (r *reader) Close() error { return r.m.close() }

// parseFrame decodes the header and copies the pixels when the sequence is
// past previous. The caller holds the producer lock.
func parseFrame(buf []byte, previous uint64) (Frame, bool, error) {
	if len(buf) < headerSize {
		return Frame{}, false, fmt.Errorf("video buffer is %d bytes, below the %d-byte header", len(buf), headerSize)
	}
	sequence := binary.LittleEndian.Uint64(buf[0:8])
	if sequence == 0 || sequence <= previous {
		return Frame{}, false, nil
	}
	width := int(int32(binary.LittleEndian.Uint32(buf[8:12])))
	height := int(int32(binary.LittleEndian.Uint32(buf[12:16])))
	if width < 1 || height < 1 || width > MaxWidth || height > MaxHeight {
		return Frame{}, false, fmt.Errorf("video buffer reports %dx%d, outside 1..%dx1..%d", width, height, MaxWidth, MaxHeight)
	}
	size := width * height * 4
	if len(buf) < headerSize+size {
		return Frame{}, false, fmt.Errorf("video buffer is %d bytes, below the %d needed for %dx%d", len(buf), headerSize+size, width, height)
	}
	captured := math.Float64frombits(binary.LittleEndian.Uint64(buf[16:24]))
	readback := math.Float64frombits(binary.LittleEndian.Uint64(buf[24:32]))
	if math.IsNaN(captured) || math.IsInf(captured, 0) || math.IsNaN(readback) || math.IsInf(readback, 0) {
		return Frame{}, false, errors.New("video buffer reports a non-finite timestamp")
	}
	data := make([]byte, size)
	copy(data, buf[headerSize:headerSize+size])
	return Frame{
		Sequence: sequence, Width: width, Height: height,
		CapturedUnixMs: int64(captured * 1000), ReadbackMs: readback,
		Selection: int32(binary.LittleEndian.Uint32(buf[32:36])), Data: data,
	}, true, nil
}
