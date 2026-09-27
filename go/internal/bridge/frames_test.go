package bridge

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

// fakeRing is a snapshot ring in memory.
type fakeRing struct {
	mu     sync.Mutex
	frames []snapshotshm.Frame
	writes int64
}

func (r *fakeRing) publish(t *testing.T, v *o.BundleSnapshot, writes int64) {
	payload, err := proto.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.frames = append(r.frames, snapshotshm.Frame{Number: uint64(len(r.frames) + 1), Writes: writes, Payload: payload})
}

func (r *fakeRing) Head() uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return uint64(len(r.frames))
}

func (r *fakeRing) Writes() int64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.writes
}

func (r *fakeRing) Latest() (snapshotshm.Frame, bool, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.frames) == 0 {
		return snapshotshm.Frame{}, false, nil
	}
	return r.frames[len(r.frames)-1], true, nil
}

func (r *fakeRing) Wait(ctx context.Context, after uint64, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) && ctx.Err() == nil {
		if r.Head() > after {
			return true
		}
		time.Sleep(time.Millisecond)
	}
	return false
}

func (r *fakeRing) Close() error { return nil }

// frameServer is the census family server that also opens the stream.
type frameServer struct {
	*bundleFamilyServer
	opens chan *o.SnapshotStreamRequest
}

func (s *frameServer) handle(ctx context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
	if arg.Tool == methodOpenSnapshotStream {
		s.opens <- nil
		return pbResult(&o.SnapshotStreamReply{Outcome: &o.SnapshotStreamReply_Opened{Opened: &o.SnapshotStreamOpened{Name: "ring", Slots: 3, SlotBytes: 1 << 20}}}), nil
	}
	return s.bundleFamilyServer.handle(ctx, arg)
}

func frameClient(t *testing.T) (*Client, *frameServer, *fakeRing) {
	t.Helper()
	server := &frameServer{bundleFamilyServer: newBundleFamilyServer(t), opens: make(chan *o.SnapshotStreamRequest, 8)}
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ring := &fakeRing{}
	client.frames = newFrameStream()
	client.frames.open = func(name string) (frameReader, error) {
		if name != "ring" {
			t.Errorf("mapped %q", name)
		}
		return ring, nil
	}
	return client, server, ring
}

// openedFrames issues one read (which starts the open) and waits for the
// ring to be mapped.
func openedFrames(t *testing.T, client *Client, server *frameServer) {
	t.Helper()
	if _, _, err := client.ReadColonyFacts(context.Background(), pbIdentity(), true, nil); err != nil {
		t.Fatal(err)
	}
	<-server.opens
	for deadline := time.Now().Add(testBudget); ; {
		client.frames.mu.Lock()
		mapped := client.frames.reader != nil
		client.frames.mu.Unlock()
		if mapped {
			return
		}
		if time.Now().After(deadline) {
			t.Fatal("ring never mapped")
		}
		time.Sleep(time.Millisecond)
	}
}

// TestFramesServeTheStateFamilies: once the stream is open, the census
// family reads are served from the newest frame without a native call; a
// shape the frame does not carry still reads natively.
func TestFramesServeTheStateFamilies(t *testing.T) {
	client, server, ring := frameClient(t)
	openedFrames(t, client, server)
	if n := server.familyReads(t, context.Background(), client); n != 4 {
		t.Fatalf("before any frame: %d native family reads, want 4", n)
	}
	ring.publish(t, server.snapshot, 0)
	if n := server.familyReads(t, context.Background(), client); n != 0 {
		t.Fatalf("from a frame: %d native family reads, want 0", n)
	}
	// A family the frame lacks reads natively.
	lacking := proto.Clone(server.snapshot).(*o.BundleSnapshot)
	lacking.Research = nil
	ring.publish(t, lacking, 0)
	if n := server.familyReads(t, context.Background(), client); n != 1 {
		t.Fatalf("frame without research: %d native family reads, want 1", n)
	}
}

// TestFramesWaitPastAWrite: after a write returns, a frame captured before
// it is not served; a frame captured past it is.
func TestFramesWaitPastAWrite(t *testing.T) {
	client, server, ring := frameClient(t)
	openedFrames(t, client, server)
	ring.publish(t, server.snapshot, 0)
	ring.mu.Lock()
	ring.writes = 1
	ring.mu.Unlock()
	client.noteFrameWrite()
	go func() {
		time.Sleep(20 * time.Millisecond)
		ring.publish(t, server.snapshot, 1)
	}()
	if n := server.familyReads(t, context.Background(), client); n != 0 {
		t.Fatalf("after a write: %d native reads, want 0 (the later frame)", n)
	}
	// A write no frame ever catches up with reads natively.
	ring.mu.Lock()
	ring.writes = 2
	ring.mu.Unlock()
	client.noteFrameWrite()
	started := time.Now()
	if _, _, err := client.ReadResearch(context.Background(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if time.Since(started) < frameWriteWait/2 {
		t.Fatal("did not wait for a frame past the write")
	}
}
