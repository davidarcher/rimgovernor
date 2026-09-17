package httpapi

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

func TestVideoLeaseStartAndStop(t *testing.T) {
	s, f, token := presentationMediaAPI(t)
	out := playerCall(s, "POST", "/api/presentation/video-lease", `{"leaseSeconds":8}`, token)
	if out.Code != 200 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	var state VideoStateDTO
	if err := json.Unmarshal(out.Body.Bytes(), &state); err != nil || !state.Active || state.SourceID == "" {
		t.Fatal(out.Body.String(), err)
	}
	request, ok := f.seen.(*p.VideoLeaseRequest)
	if !ok || request.GetStart().GetLeaseSeconds() != 8 || request.GetStart().GetViewer().GetIdentity().GetColonyId() != "colony" {
		t.Fatal(f.seen)
	}

	out = playerCall(s, "POST", "/api/presentation/video-lease", `{"leaseSeconds":0}`, token)
	if out.Code != 200 || f.calls != 2 {
		t.Fatal(out.Code, out.Body.String())
	}
	request, ok = f.seen.(*p.VideoLeaseRequest)
	if !ok || request.GetStop() == nil {
		t.Fatal("expected a stop operation", f.seen)
	}
}
func TestVideoLeaseValidationAndAuth(t *testing.T) {
	s, f, token := presentationMediaAPI(t)
	for _, body := range []string{`{}`, `{"leaseSeconds":16}`, `{"leaseSeconds":1,"extra":true}`} {
		out := playerCall(s, "POST", "/api/presentation/video-lease", body, token)
		if out.Code != 400 || f.calls != 0 {
			t.Fatal(body, out.Code, out.Body.String())
		}
	}
	out := playerCall(s, "POST", "/api/presentation/video-lease", `{"leaseSeconds":1}`, "")
	if out.Code != 403 || f.calls != 0 {
		t.Fatal(out.Code)
	}
	get := playerCall(s, "GET", "/api/presentation/video-lease", "", token)
	if get.Code != 405 {
		t.Fatal(get.Code)
	}
}
func TestVideoTicketRequiresPlayerToken(t *testing.T) {
	s, _, token := presentationMediaAPI(t)
	out := playerCall(s, "POST", "/api/presentation/video-stream/ticket", "", "")
	if out.Code != 403 {
		t.Fatal(out.Code)
	}
	out = playerCall(s, "POST", "/api/presentation/video-stream/ticket", "", token)
	if out.Code != 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	var body videoTicketDTO
	if err := json.Unmarshal(out.Body.Bytes(), &body); err != nil || len(body.Ticket) != 48 {
		t.Fatal(out.Body.String(), err)
	}
}

// videoStreamServer wires the same fixtures as presentationMediaAPI onto a
// real listening httptest.Server, needed because a WebSocket upgrade requires
// an actual hijacked TCP connection rather than an in-memory ResponseRecorder.
func videoStreamServer(t *testing.T) (*httptest.Server, *presentationMediaFake, string) {
	t.Helper()
	server, _, f, token := videoStreamAPI(t)
	return server, f, token
}

// videoStreamAPI also exposes the Server so a test can install a
// shared-memory opener before connecting.
func videoStreamAPI(t *testing.T) (*httptest.Server, *Server, *presentationMediaFake, string) {
	t.Helper()
	s, f, token := presentationMediaAPI(t)
	s.config.VideoStreamPollInterval = 2 * time.Millisecond
	server := httptest.NewServer(s.Handler())
	t.Cleanup(server.Close)
	return server, s, f, token
}
func mintTicket(t *testing.T, server *httptest.Server, token string) string {
	t.Helper()
	req, err := http.NewRequest("POST", server.URL+"/api/presentation/video-stream/ticket", nil)
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("X-RimGovernor-Player", token)
	resp, err := server.Client().Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatal(resp.StatusCode)
	}
	var body videoTicketDTO
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	return body.Ticket
}
func wsURL(server *httptest.Server, ticket string) string {
	return "ws" + strings.TrimPrefix(server.URL, "http") + "/api/presentation/video-stream?ticket=" + ticket
}

func TestVideoStreamConnectAndForwardsFrames(t *testing.T) {
	server, f, token := videoStreamServer(t)
	f.frames = []*p.FrameReply{
		{Outcome: &p.FrameReply_Frame{Frame: &p.MediaFrame{
			Frame: &p.FrameReference{SourceId: proto.String("s"), Sequence: proto.Uint64(1)},
			Width: proto.Uint32(2), Height: proto.Uint32(1), Encoding: p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP.Enum(),
			CaptureMethod: p.CaptureMethod_CAPTURE_METHOD_READ_PIXELS.Enum(), CapturedUnixMs: proto.Int64(1700000000000), ReadbackMs: proto.Float64(2),
			Data: []byte{9, 8, 7, 6, 5, 4, 4, 3},
		}}},
		{Outcome: &p.FrameReply_Frame{Frame: &p.MediaFrame{
			Frame: &p.FrameReference{SourceId: proto.String("s"), Sequence: proto.Uint64(2)},
			Width: proto.Uint32(2), Height: proto.Uint32(1), Encoding: p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP.Enum(),
			CaptureMethod: p.CaptureMethod_CAPTURE_METHOD_READ_PIXELS.Enum(), CapturedUnixMs: proto.Int64(1700000000033), ReadbackMs: proto.Float64(2),
			Data: []byte{1, 2, 3, 4, 5, 6, 7, 8},
		}}},
	}
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(websocket.StatusNormalClosure, "")

	var sequences []uint64
	for i := 0; i < 2; i++ {
		readCtx, cancelRead := context.WithTimeout(ctx, 3*time.Second)
		typ, data, err := conn.Read(readCtx)
		cancelRead()
		if err != nil {
			t.Fatal(err)
		}
		if typ != websocket.MessageBinary || len(data) < 34 {
			t.Fatal(typ, len(data))
		}
		sequences = append(sequences, binary.LittleEndian.Uint64(data[0:8]))
		width := binary.LittleEndian.Uint32(data[8:12])
		height := binary.LittleEndian.Uint32(data[12:16])
		if width != 2 || height != 1 {
			t.Fatal(width, height)
		}
		payload := data[34:]
		if len(payload) != 8 {
			t.Fatal("payload length mismatch", len(payload))
		}
	}
	if sequences[0] != 1 || sequences[1] != 2 {
		t.Fatal("expected strictly increasing sequence, no duplicates", sequences)
	}
	if f.ackCalls < 2 {
		t.Fatal("expected an AcknowledgeFrame call per forwarded frame", f.ackCalls)
	}
}
func TestVideoStreamTicketIsSingleUse(t *testing.T) {
	server, _, token := videoStreamServer(t)
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err != nil {
		t.Fatal(err)
	}
	conn.Close(websocket.StatusNormalClosure, "")

	_, resp, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err == nil {
		t.Fatal("reused ticket accepted a second connection")
	}
	if resp != nil && resp.StatusCode != 403 {
		t.Fatal(resp.StatusCode)
	}
}
func TestVideoStreamRejectsMissingOrUnknownTicket(t *testing.T) {
	server, _, _ := videoStreamServer(t)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, resp, err := websocket.Dial(ctx, wsURL(server, ""), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}}); err == nil {
		t.Fatal("missing ticket accepted")
	} else if resp != nil && resp.StatusCode != 400 {
		t.Fatal(resp.StatusCode)
	}
	unknown := "ab" + strings.Repeat("00", 23)
	if _, resp, err := websocket.Dial(ctx, wsURL(server, unknown), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}}); err == nil {
		t.Fatal("unknown ticket accepted")
	} else if resp != nil && resp.StatusCode != 403 {
		t.Fatal(resp.StatusCode)
	}
}
func TestVideoStreamRejectsCrossOrigin(t *testing.T) {
	server, _, token := videoStreamServer(t)
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, resp, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {"http://evil.example"}}}); err == nil {
		t.Fatal("cross-origin upgrade accepted")
	} else if resp != nil && resp.StatusCode != 403 {
		t.Fatal(resp.StatusCode)
	}
}
func TestVideoStreamRejectsMissingOrigin(t *testing.T) {
	server, _, token := videoStreamServer(t)
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, resp, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{}); err == nil {
		t.Fatal("missing Origin accepted")
	} else if resp != nil && resp.StatusCode != 403 {
		t.Fatal(resp.StatusCode)
	}
}
func TestVideoStreamStopsOnLeaseError(t *testing.T) {
	server, f, token := videoStreamServer(t)
	f.err = context.DeadlineExceeded
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err != nil {
		t.Fatal(err)
	}
	readCtx, cancelRead := context.WithTimeout(ctx, 3*time.Second)
	defer cancelRead()
	if _, _, err := conn.Read(readCtx); err == nil {
		t.Fatal("expected the server to close the stream when ReadFrame fails")
	}
}

// sharedFrames is an in-memory videoshm.Reader whose frames the test
// publishes directly, standing in for the native buffer.
type sharedFrames struct {
	mu     sync.Mutex
	frames []videoshm.Frame
	closed bool
}

func (r *sharedFrames) publish(sequence uint64, pixels []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.frames = append(r.frames, videoshm.Frame{Sequence: sequence, Width: 2, Height: 1, CapturedUnixMs: 1700000000000 + int64(sequence), ReadbackMs: 1, Data: pixels})
}
func (r *sharedFrames) Read(previous uint64) (videoshm.Frame, bool, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.frames) == 0 {
		return videoshm.Frame{}, false, nil
	}
	latest := r.frames[len(r.frames)-1]
	if latest.Sequence <= previous {
		return videoshm.Frame{}, false, nil
	}
	return latest, true, nil
}
func (r *sharedFrames) Close() error { r.mu.Lock(); defer r.mu.Unlock(); r.closed = true; return nil }

func rpcFrame(source string, sequence uint64, pixels []byte) *p.FrameReply {
	return &p.FrameReply{Outcome: &p.FrameReply_Frame{Frame: &p.MediaFrame{
		Frame: &p.FrameReference{SourceId: proto.String(source), Sequence: proto.Uint64(sequence)},
		Width: proto.Uint32(2), Height: proto.Uint32(1), Encoding: p.MediaEncoding_MEDIA_ENCODING_BGRA32_TOP_DOWN.Enum(),
		CaptureMethod: p.CaptureMethod_CAPTURE_METHOD_ASYNC_GPU.Enum(), CapturedUnixMs: proto.Int64(1700000000000), ReadbackMs: proto.Float64(2),
		Data: pixels,
	}}}
}

func readFrameMessage(t *testing.T, ctx context.Context, conn *websocket.Conn) (sequence uint64, encoding byte, payload []byte) {
	t.Helper()
	readCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	typ, data, err := conn.Read(readCtx)
	if err != nil {
		t.Fatal(err)
	}
	if typ != websocket.MessageBinary || len(data) < 34 {
		t.Fatal(typ, len(data))
	}
	return binary.LittleEndian.Uint64(data[0:8]), data[16], data[34:]
}

func TestVideoStreamReadsSharedMemoryAfterFirstFrame(t *testing.T) {
	server, api, f, token := videoStreamAPI(t)
	f.frames = []*p.FrameReply{rpcFrame(`Local\RimGovernorVideo-a`, 1, []byte{1, 1, 1, 1, 1, 1, 1, 1})}
	shared := &sharedFrames{}
	var opened []string
	api.config.VideoFrames = func(source string) (videoshm.Reader, error) {
		opened = append(opened, source)
		return shared, nil
	}
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(websocket.StatusNormalClosure, "")

	if sequence, _, _ := readFrameMessage(t, ctx, conn); sequence != 1 {
		t.Fatal("first frame comes from ReadFrame", sequence)
	}
	shared.publish(2, []byte{2, 2, 2, 2, 2, 2, 2, 2})
	sequence, encoding, payload := readFrameMessage(t, ctx, conn)
	if sequence != 2 || !bytes.Equal(payload, []byte{2, 2, 2, 2, 2, 2, 2, 2}) {
		t.Fatal(sequence, payload)
	}
	if encoding != byte(p.MediaEncoding_MEDIA_ENCODING_BGRA32_TOP_DOWN) {
		t.Fatal("shared frames inherit the ReadFrame reply's encoding", encoding)
	}
	shared.publish(3, []byte{3, 3, 3, 3, 3, 3, 3, 3})
	if sequence, _, _ := readFrameMessage(t, ctx, conn); sequence != 3 {
		t.Fatal(sequence)
	}
	if len(opened) != 1 || opened[0] != `Local\RimGovernorVideo-a` {
		t.Fatal("the buffer named by the first ReadFrame reply is opened once", opened)
	}
	if f.frameCalls != 1 {
		t.Fatal("shared frames must not cost ReadFrame round trips within the reconcile window", f.frameCalls)
	}
	if f.ackCalls != 1 {
		t.Fatal("only the ReadFrame-delivered frame is acknowledged", f.ackCalls)
	}
}

func TestVideoStreamReconcilesSourceChangeThroughReadFrame(t *testing.T) {
	server, api, f, token := videoStreamAPI(t)
	f.frames = []*p.FrameReply{rpcFrame("first", 1, []byte{1, 1, 1, 1, 1, 1, 1, 1}), rpcFrame("second", 1, []byte{9, 9, 9, 9, 9, 9, 9, 9})}
	first, second := &sharedFrames{}, &sharedFrames{}
	api.config.VideoFrames = func(source string) (videoshm.Reader, error) {
		if source == "first" {
			return first, nil
		}
		return second, nil
	}
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(websocket.StatusNormalClosure, "")
	if sequence, _, _ := readFrameMessage(t, ctx, conn); sequence != 1 {
		t.Fatal(sequence)
	}
	first.publish(5, []byte{5, 5, 5, 5, 5, 5, 5, 5})
	if sequence, _, _ := readFrameMessage(t, ctx, conn); sequence != 5 {
		t.Fatal(sequence)
	}
	// After the reconcile window ReadFrame names a new source whose sequence
	// restarted; the old buffer is closed and the new one takes over.
	second.publish(2, []byte{2, 2, 2, 2, 2, 2, 2, 2})
	deadline := time.Now().Add(4 * time.Second)
	for {
		sequence, _, payload := readFrameMessage(t, ctx, conn)
		if sequence == 2 && bytes.Equal(payload, []byte{2, 2, 2, 2, 2, 2, 2, 2}) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("expected the relay to switch to the second source", sequence)
		}
	}
	first.mu.Lock()
	closed := first.closed
	first.mu.Unlock()
	if !closed {
		t.Fatal("the replaced buffer must be closed")
	}
}

func TestVideoStreamFallsBackToReadFrameWhenSharedMemoryIsUnavailable(t *testing.T) {
	server, api, f, token := videoStreamAPI(t)
	f.frames = []*p.FrameReply{rpcFrame("s", 1, []byte{1, 1, 1, 1, 1, 1, 1, 1}), rpcFrame("s", 2, []byte{2, 2, 2, 2, 2, 2, 2, 2})}
	api.config.VideoFrames = func(string) (videoshm.Reader, error) { return nil, videoshm.ErrUnavailable }
	ticket := mintTicket(t, server, token)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL(server, ticket), &websocket.DialOptions{HTTPHeader: http.Header{"Origin": {server.URL}}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(websocket.StatusNormalClosure, "")
	for want := uint64(1); want <= 2; want++ {
		if sequence, _, _ := readFrameMessage(t, ctx, conn); sequence != want {
			t.Fatal(want, sequence)
		}
	}
	if f.ackCalls < 2 {
		t.Fatal("RPC-delivered frames are still acknowledged", f.ackCalls)
	}
}
