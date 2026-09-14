package httpapi

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"mime"
	"net/http"
	"net/url"
	"time"

	"github.com/coder/websocket"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// The browser WebSocket API cannot set custom request headers on the upgrade
// handshake, so the long-lived X-RimGovernor-Player token (used everywhere
// else) cannot gate this connection directly. Instead an authenticated POST
// mints a short-lived, single-use ticket; the WebSocket URL carries only that
// ticket, which is invalidated on first use (or after videoTicketWindow).
const videoTicketWindow = 5 * time.Second

// defaultVideoPollInterval proves correctness (increasing sequence, no
// duplicate/stale frames), not maximum achievable throughput.
const defaultVideoPollInterval = 40 * time.Millisecond

type videoLeaseRequestDTO struct {
	LeaseSeconds *uint32 `json:"leaseSeconds"`
}
type VideoStateDTO struct {
	Supported        bool    `json:"supported"`
	Active           bool    `json:"active"`
	SourceID         string  `json:"sourceId"`
	RemainingLeaseMs uint32  `json:"remainingLeaseMs"`
	CapturedFrames   uint64  `json:"capturedFrames"`
	FramesPerSecond  float64 `json:"framesPerSecond"`
	PixelFormat      string  `json:"pixelFormat"`
	CaptureMethod    string  `json:"captureMethod"`
}
type videoTicketDTO struct {
	Ticket    string `json:"ticket"`
	ExpiresMs int64  `json:"expiresMs"`
}

func (s *Server) handleVideoStream(w http.ResponseWriter, r *http.Request) bool {
	switch r.URL.Path {
	case "/api/presentation/video-lease", "/api/presentation/video-stream/ticket", "/api/presentation/video-stream":
	default:
		return false
	}
	if s.config.PresentationMedia == nil || s.playerToken == "" {
		s.failure(w, r, 404, "not_found", "Video streaming is unavailable")
		return true
	}
	switch r.URL.Path {
	case "/api/presentation/video-lease":
		s.handleVideoLease(w, r)
	case "/api/presentation/video-stream/ticket":
		s.handleVideoTicket(w, r)
	case "/api/presentation/video-stream":
		s.handleVideoSocket(w, r)
	}
	return true
}

// playerTokenGated enforces the same X-RimGovernor-Player check as other
// player mutations, plus a bounded JSON POST body.
func (s *Server) playerTokenGated(w http.ResponseWriter, r *http.Request) bool {
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", "POST")
		s.failure(w, r, 405, "method_not_allowed", "This route requires POST")
		return false
	}
	tokens := r.Header.Values("X-RimGovernor-Player")
	if len(tokens) != 1 || subtle.ConstantTimeCompare([]byte(tokens[0]), []byte(s.playerToken)) != 1 {
		s.failure(w, r, 403, "player_auth", "Player session token required")
		return false
	}
	if r.URL.RawQuery != "" || r.URL.ForceQuery || r.ContentLength > buildingRequestLimit {
		s.failure(w, r, 400, "invalid_request", "Mutation requires a bounded body and no query")
		return false
	}
	return true
}

func (s *Server) handleVideoLease(w http.ResponseWriter, r *http.Request) {
	if !s.playerTokenGated(w, r) {
		return
	}
	kind, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || kind != "application/json" {
		s.failure(w, r, 415, "content_type", "Use application/json")
		return
	}
	var body videoLeaseRequestDTO
	if err := decodeMediaBody(r, &body); err != nil || body.LeaseSeconds == nil || *body.LeaseSeconds > 15 {
		s.failure(w, r, 400, "invalid_request", "leaseSeconds (0-15) is required")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	identity, err := s.presentationIdentity(ctx)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	wire := &c.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}
	viewer := &p.PlayerIdentity{Identity: wire}
	var request *p.VideoLeaseRequest
	if *body.LeaseSeconds > 0 {
		request = &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Start{Start: &p.VideoStart{Viewer: viewer, LeaseSeconds: proto.Uint32(*body.LeaseSeconds)}}}
	} else {
		request = &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Stop{Stop: &p.VideoStop{Viewer: viewer}}}
	}
	reply, _, err := s.config.PresentationMedia.LeaseVideo(ctx, request)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	state := reply.GetState()
	s.write(w, r, 200, VideoStateDTO{
		Supported: state.GetSupported(), Active: state.GetActive(), SourceID: state.GetSourceId(),
		RemainingLeaseMs: state.GetRemainingLeaseMs(), CapturedFrames: state.GetCapturedFrames(),
		FramesPerSecond: state.GetFramesPerSecond(), PixelFormat: state.GetPixelFormat().String(), CaptureMethod: state.GetCaptureMethod().String(),
	})
}

func (s *Server) handleVideoTicket(w http.ResponseWriter, r *http.Request) {
	if !s.playerTokenGated(w, r) {
		return
	}
	var raw [24]byte
	if _, err := rand.Read(raw[:]); err != nil {
		s.failure(w, r, 503, "unavailable", "Unable to mint a video stream ticket")
		return
	}
	ticket := hex.EncodeToString(raw[:])
	expiry := time.Now().Add(videoTicketWindow)
	s.sweepVideoTickets()
	s.videoTickets.Store(ticket, expiry)
	s.write(w, r, 200, videoTicketDTO{Ticket: ticket, ExpiresMs: expiry.UnixMilli()})
}

// sweepVideoTickets drops expired, unused tickets so an abandoned mint never
// accumulates; this is a local single-player controller, so a full scan is cheap.
func (s *Server) sweepVideoTickets() {
	now := time.Now()
	s.videoTickets.Range(func(key, value any) bool {
		if expiry, ok := value.(time.Time); !ok || now.After(expiry) {
			s.videoTickets.Delete(key)
		}
		return true
	})
}

// consumeVideoTicket atomically removes and validates a ticket: a ticket is
// good for exactly one WebSocket connection attempt within its short window.
func (s *Server) consumeVideoTicket(ticket string) bool {
	value, ok := s.videoTickets.LoadAndDelete(ticket)
	if !ok {
		return false
	}
	expiry, ok := value.(time.Time)
	return ok && time.Now().Before(expiry)
}

func (s *Server) handleVideoSocket(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.Header().Set("Allow", "GET")
		s.failure(w, r, 405, "method_not_allowed", "Video stream requires GET")
		return
	}
	// Defense-in-depth on top of handle()'s generic checks: a native browser
	// WebSocket handshake always carries Origin, and this stateful, resource-
	// holding connection should never accept a missing or non-matching one.
	origin := r.Header.Get("Origin")
	parsed, err := url.Parse(origin)
	if origin == "" || err != nil || parsed.Scheme != "http" || !equalFoldHost(parsed.Host, r.Host) {
		s.failure(w, r, 403, "cross_origin", "Origin must match this local controller")
		return
	}
	query, err := url.ParseQuery(r.URL.RawQuery)
	tickets := query["ticket"]
	if err != nil || len(query) != 1 || len(tickets) != 1 || len(tickets[0]) != 48 || !isHex(tickets[0]) {
		s.failure(w, r, 400, "invalid_request", "A single valid ticket query parameter is required")
		return
	}
	if !s.consumeVideoTicket(tickets[0]) {
		s.failure(w, r, 403, "player_auth", "Video stream ticket is invalid, expired or already used")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	identity, err := s.presentationIdentity(ctx)
	cancel()
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	wire := &c.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}
	viewer := &p.PlayerIdentity{Identity: wire}

	// Origin/host were already verified above; the ticket was the actual
	// authentication step, so the library's own same-origin default check
	// would be redundant with what this handler already enforced.
	conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
	if err != nil {
		return
	}
	defer conn.CloseNow()
	streamCtx := conn.CloseRead(context.Background())
	s.runVideoStream(streamCtx, conn, viewer)
}

func equalFoldHost(a, b string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := 0; i < len(a); i++ {
		ca, cb := a[i], b[i]
		if 'A' <= ca && ca <= 'Z' {
			ca += 'a' - 'A'
		}
		if 'A' <= cb && cb <= 'Z' {
			cb += 'a' - 'A'
		}
		if ca != cb {
			return false
		}
	}
	return true
}
func isHex(v string) bool {
	for _, r := range v {
		if !(r >= '0' && r <= '9') && !(r >= 'a' && r <= 'f') && !(r >= 'A' && r <= 'F') {
			return false
		}
	}
	return true
}

// runVideoStream polls ReadFrame at a fixed interval and forwards each
// genuinely-new frame (by strictly increasing sequence) as one binary
// WebSocket message: a 34-byte header (sequence uint64, width/height uint32,
// encoding/captureMethod uint8, capturedUnixMs int64, readbackMs float64, all
// little-endian) followed by the raw pixel bytes. It stops cleanly when the
// context is done (client disconnect or handler shutdown), the lease ends, or
// a write fails.
func (s *Server) runVideoStream(ctx context.Context, conn *websocket.Conn, viewer *p.PlayerIdentity) {
	interval := s.config.VideoStreamPollInterval
	if interval <= 0 {
		interval = defaultVideoPollInterval
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	var lastSequence uint64
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
		readCtx, cancel := context.WithTimeout(ctx, s.config.ReadTimeout)
		reply, _, err := s.config.PresentationMedia.ReadFrame(readCtx, &p.FrameRequest{Viewer: viewer})
		cancel()
		if err != nil {
			_ = conn.Close(websocket.StatusNormalClosure, "video lease ended")
			return
		}
		frame := reply.GetFrame()
		ref := frame.GetFrame()
		if ref == nil || ref.GetSequence() == lastSequence {
			continue
		}
		lastSequence = ref.GetSequence()
		writeCtx, cancelWrite := context.WithTimeout(ctx, s.config.ReadTimeout)
		err = conn.Write(writeCtx, websocket.MessageBinary, encodeVideoFrameMessage(frame))
		cancelWrite()
		if err != nil {
			return
		}
		ackCtx, cancelAck := context.WithTimeout(ctx, s.config.ReadTimeout)
		_, _, _ = s.config.PresentationMedia.AcknowledgeFrame(ackCtx, &p.FrameAcknowledgement{
			Viewer: viewer, Frame: ref, DisplayedUnixMs: proto.Int64(time.Now().UnixMilli()),
		})
		cancelAck()
	}
}

func encodeVideoFrameMessage(frame *p.MediaFrame) []byte {
	var header bytes.Buffer
	header.Grow(34 + len(frame.GetData()))
	_ = binary.Write(&header, binary.LittleEndian, frame.GetFrame().GetSequence())
	_ = binary.Write(&header, binary.LittleEndian, frame.GetWidth())
	_ = binary.Write(&header, binary.LittleEndian, frame.GetHeight())
	header.WriteByte(byte(frame.GetEncoding()))
	header.WriteByte(byte(frame.GetCaptureMethod()))
	_ = binary.Write(&header, binary.LittleEndian, frame.GetCapturedUnixMs())
	_ = binary.Write(&header, binary.LittleEndian, frame.GetReadbackMs())
	header.Write(frame.GetData())
	return header.Bytes()
}
