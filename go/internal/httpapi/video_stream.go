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
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
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

// VideoSourceDTO names what a lease captures: the presented screen (the
// default), a colonist followed by a second camera, or the whole map.
type VideoSourceDTO struct {
	Kind            string  `json:"kind"`
	PawnID          string  `json:"pawnId,omitempty"`
	Width           uint32  `json:"width,omitempty"`
	Height          uint32  `json:"height,omitempty"`
	FramesPerSecond float64 `json:"framesPerSecond,omitempty"`
}
type videoLeaseRequestDTO struct {
	LeaseSeconds *uint32         `json:"leaseSeconds"`
	Source       *VideoSourceDTO `json:"source,omitempty"`
	// SourceID selects the source a stop (leaseSeconds 0) ends; absent stops all.
	SourceID string `json:"sourceId,omitempty"`
}
type VideoStateDTO struct {
	Supported bool           `json:"supported"`
	Active    bool           `json:"active"`
	SourceID  string         `json:"sourceId"`
	Source    VideoSourceDTO `json:"source"`
	// Unavailable says why a supported lease is not active: the pawn is not
	// on the current map, or there is no map yet.
	Unavailable      string  `json:"unavailable,omitempty"`
	RemainingLeaseMs uint32  `json:"remainingLeaseMs"`
	CapturedFrames   uint64  `json:"capturedFrames"`
	FramesPerSecond  float64 `json:"framesPerSecond"`
	PixelFormat      string  `json:"pixelFormat"`
	CaptureMethod    string  `json:"captureMethod"`
}
type videoTicketRequestDTO struct {
	// SourceID binds the ticket's stream to one leased source; absent means
	// the screen.
	SourceID string `json:"sourceId,omitempty"`
}
type videoTicketDTO struct {
	Ticket    string `json:"ticket"`
	ExpiresMs int64  `json:"expiresMs"`
}
type videoTicket struct {
	expiry   time.Time
	sourceID string
}

var videoSourceKinds = map[string]p.VideoSourceKind{
	"screen": p.VideoSourceKind_VIDEO_SOURCE_KIND_SCREEN,
	"pawn":   p.VideoSourceKind_VIDEO_SOURCE_KIND_PAWN,
	"map":    p.VideoSourceKind_VIDEO_SOURCE_KIND_MAP,
}

func videoSourceWire(dto *VideoSourceDTO) (*p.VideoSource, bool) {
	if dto == nil {
		return nil, true
	}
	kind, ok := videoSourceKinds[dto.Kind]
	if !ok || (kind == p.VideoSourceKind_VIDEO_SOURCE_KIND_PAWN) != (dto.PawnID != "") || len(dto.PawnID) > 256 {
		return nil, false
	}
	if dto.Width > 3840 || dto.Height > 2160 || dto.FramesPerSecond < 0 || dto.FramesPerSecond > 60 {
		return nil, false
	}
	source := &p.VideoSource{Kind: kind.Enum()}
	if dto.PawnID != "" {
		source.PawnId = proto.String(dto.PawnID)
	}
	if dto.Width > 0 {
		source.Width = proto.Uint32(dto.Width)
	}
	if dto.Height > 0 {
		source.Height = proto.Uint32(dto.Height)
	}
	if dto.FramesPerSecond > 0 {
		source.FramesPerSecond = proto.Float64(dto.FramesPerSecond)
	}
	return source, true
}

func videoSourceDTO(source *p.VideoSource) VideoSourceDTO {
	dto := VideoSourceDTO{Kind: "screen", PawnID: source.GetPawnId(), Width: source.GetWidth(), Height: source.GetHeight(), FramesPerSecond: source.GetFramesPerSecond()}
	for name, kind := range videoSourceKinds {
		if kind == source.GetKind() {
			dto.Kind = name
		}
	}
	return dto
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
	if err := decodeMediaBody(r, &body); err != nil || body.LeaseSeconds == nil || *body.LeaseSeconds > 15 || len(body.SourceID) > 256 {
		s.failure(w, r, 400, "invalid_request", "leaseSeconds (0-15) is required")
		return
	}
	source, ok := videoSourceWire(body.Source)
	if !ok {
		s.failure(w, r, 400, "invalid_request", "source must be screen, pawn (with pawnId) or map, with a bounded size and frame rate")
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
		request = &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Start{Start: &p.VideoStart{Viewer: viewer, LeaseSeconds: proto.Uint32(*body.LeaseSeconds), Source: source}}}
	} else {
		stop := &p.VideoStop{Viewer: viewer}
		if body.SourceID != "" {
			stop.SourceId = proto.String(body.SourceID)
		}
		request = &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Stop{Stop: stop}}
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
		Supported: state.GetSupported(), Active: state.GetActive(), SourceID: state.GetSourceId(), Source: videoSourceDTO(state.GetSource()), Unavailable: state.GetUnavailable().GetDetail(),
		RemainingLeaseMs: state.GetRemainingLeaseMs(), CapturedFrames: state.GetCapturedFrames(),
		FramesPerSecond: state.GetFramesPerSecond(), PixelFormat: state.GetPixelFormat().String(), CaptureMethod: state.GetCaptureMethod().String(),
	})
}

func (s *Server) handleVideoTicket(w http.ResponseWriter, r *http.Request) {
	if !s.playerTokenGated(w, r) {
		return
	}
	var body videoTicketRequestDTO
	if r.ContentLength != 0 {
		if err := decodeMediaBody(r, &body); err != nil || len(body.SourceID) > 256 {
			s.failure(w, r, 400, "invalid_request", "sourceId must be a leased source id")
			return
		}
	}
	var raw [24]byte
	if _, err := rand.Read(raw[:]); err != nil {
		s.failure(w, r, 503, "unavailable", "Unable to mint a video stream ticket")
		return
	}
	ticket := hex.EncodeToString(raw[:])
	expiry := time.Now().Add(videoTicketWindow)
	s.sweepVideoTickets()
	s.videoTickets.Store(ticket, videoTicket{expiry: expiry, sourceID: body.SourceID})
	s.write(w, r, 200, videoTicketDTO{Ticket: ticket, ExpiresMs: expiry.UnixMilli()})
}

// sweepVideoTickets drops expired, unused tickets so an abandoned mint never
// accumulates; this is a local single-player controller, so a full scan is cheap.
func (s *Server) sweepVideoTickets() {
	now := time.Now()
	s.videoTickets.Range(func(key, value any) bool {
		if ticket, ok := value.(videoTicket); !ok || now.After(ticket.expiry) {
			s.videoTickets.Delete(key)
		}
		return true
	})
}

// consumeVideoTicket atomically removes and validates a ticket: a ticket is
// good for exactly one WebSocket connection attempt within its short window,
// and names the source that connection streams.
func (s *Server) consumeVideoTicket(ticket string) (string, bool) {
	value, ok := s.videoTickets.LoadAndDelete(ticket)
	if !ok {
		return "", false
	}
	minted, ok := value.(videoTicket)
	if !ok || !time.Now().Before(minted.expiry) {
		return "", false
	}
	return minted.sourceID, true
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
	sourceID, ok := s.consumeVideoTicket(tickets[0])
	if !ok {
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
	s.runVideoStream(streamCtx, conn, viewer, sourceID)
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

// runVideoStream forwards each genuinely-new frame (by strictly increasing
// sequence) as one binary WebSocket message: a 34-byte header (sequence
// uint64, width/height uint32, encoding/captureMethod uint8, capturedUnixMs
// int64, readbackMs float64, all little-endian) followed by the raw pixel
// bytes. It stops cleanly when the context is done (client disconnect or
// handler shutdown), the lease ends, or a write fails.
//
// The stream is bound to the source its ticket named (empty: the screen);
// ReadFrame asks for that source, so the socket closes when that lease ends.
// Frames come from ReadFrame until the first reply names the source; when
// Config.VideoFrames can open that source's shared-memory buffer, later
// frames are read from it directly and ReadFrame is only called once per
// videoSharedReconcile to confirm the lease is still alive and the source
// unchanged (a new lease publishes under a new name). Frames read from the
// buffer inherit the encoding and capture method of that ReadFrame reply,
// which the buffer header does not carry. AcknowledgeFrame is telemetry
// only, so only ReadFrame-delivered frames are acknowledged; shared-memory
// frames never cost a round trip.
func (s *Server) runVideoStream(ctx context.Context, conn *websocket.Conn, viewer *p.PlayerIdentity, wantSource string) {
	request := &p.FrameRequest{Viewer: viewer}
	if wantSource != "" {
		request.SourceId = proto.String(wantSource)
	}
	interval := s.config.VideoStreamPollInterval
	if interval <= 0 {
		interval = defaultVideoPollInterval
	}
	// The buffer is cheap to poll, so it is sampled faster than the RPC; the
	// native driver publishes at most 60 frames per second.
	sharedInterval := max(interval/4, time.Millisecond)
	ticker := time.NewTicker(sharedInterval)
	defer ticker.Stop()
	var sourceID string
	var lastSequence uint64
	var shared videoshm.Reader
	var template *p.MediaFrame
	var lastRPC time.Time
	defer func() {
		if shared != nil {
			_ = shared.Close()
		}
	}()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
		var frame *p.MediaFrame
		fromRPC := false
		if shared != nil && time.Since(lastRPC) < videoSharedReconcile {
			raw, ok, err := shared.Read(lastSequence)
			if err != nil {
				// A torn or foreign buffer: drop it and let the next RPC decide.
				_ = shared.Close()
				shared = nil
				continue
			}
			if !ok {
				continue
			}
			frame = sharedVideoFrame(template, raw)
		} else {
			if shared == nil && time.Since(lastRPC) < interval {
				continue
			}
			readCtx, cancel := context.WithTimeout(ctx, s.config.ReadTimeout)
			reply, _, err := s.config.PresentationMedia.ReadFrame(readCtx, request)
			cancel()
			if err != nil {
				_ = conn.Close(websocket.StatusNormalClosure, "video lease ended")
				return
			}
			lastRPC = time.Now()
			frame = reply.GetFrame()
			ref := frame.GetFrame()
			if ref == nil {
				continue
			}
			// Sequences restart with each lease's new source.
			if ref.GetSourceId() != sourceID {
				sourceID, lastSequence = ref.GetSourceId(), 0
				if shared != nil {
					_ = shared.Close()
					shared = nil
				}
			}
			if shared == nil && s.config.VideoFrames != nil {
				if reader, err := s.config.VideoFrames(sourceID); err == nil {
					shared, template = reader, frame
				}
			}
			// The buffer may already be ahead of this reply.
			if ref.GetSequence() <= lastSequence {
				continue
			}
			fromRPC = true
		}
		lastSequence = frame.GetFrame().GetSequence()
		writeCtx, cancelWrite := context.WithTimeout(ctx, s.config.ReadTimeout)
		err := conn.Write(writeCtx, websocket.MessageBinary, encodeVideoFrameMessage(frame))
		cancelWrite()
		if err != nil {
			return
		}
		if !fromRPC {
			continue
		}
		ackCtx, cancelAck := context.WithTimeout(ctx, s.config.ReadTimeout)
		_, _, _ = s.config.PresentationMedia.AcknowledgeFrame(ackCtx, &p.FrameAcknowledgement{
			Viewer: viewer, Frame: frame.GetFrame(), DisplayedUnixMs: proto.Int64(time.Now().UnixMilli()),
		})
		cancelAck()
	}
}

// videoSharedReconcile bounds how long the relay trusts the shared buffer
// before confirming the lease through ReadFrame again.
const videoSharedReconcile = time.Second

// sharedVideoFrame projects a buffer frame onto the wire shape, taking the
// source, encoding and capture method from the ReadFrame reply that opened it.
func sharedVideoFrame(template *p.MediaFrame, raw videoshm.Frame) *p.MediaFrame {
	return &p.MediaFrame{
		Frame:          &p.FrameReference{SourceId: proto.String(template.GetFrame().GetSourceId()), Sequence: proto.Uint64(raw.Sequence)},
		Width:          proto.Uint32(uint32(raw.Width)),
		Height:         proto.Uint32(uint32(raw.Height)),
		Encoding:       template.GetEncoding().Enum(),
		CaptureMethod:  template.GetCaptureMethod().Enum(),
		CapturedUnixMs: proto.Int64(raw.CapturedUnixMs),
		ReadbackMs:     proto.Float64(raw.ReadbackMs),
		Data:           raw.Data,
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
