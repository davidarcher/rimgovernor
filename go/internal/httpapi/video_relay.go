package httpapi

import (
	"context"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// videoRelay shares one frame reader per leased source among every socket
// streaming it (#631). Frames are read from the shared buffer (or the
// ReadFrame fallback) once per source, not once per client, and handed to
// each subscriber through a one-frame queue: a subscriber whose socket is
// not draining keeps only the newest frame, so a stalled or hidden viewer
// costs the source nothing and never delays another viewer's frames. The
// source's reader ends with its last subscriber or when the lease ends.
type videoRelay struct {
	mu      sync.Mutex
	sources map[string]*videoRelaySource
}

type videoRelaySource struct {
	// key is the source the sockets asked for ("" is the screen); the
	// native source id it resolves to is learned from ReadFrame.
	key         string
	viewer      *p.PlayerIdentity
	subscribers map[*videoSubscriber]struct{}
	// ended closes when the lease ends (ReadFrame failed): subscribers
	// close their sockets with the lease-ended status.
	ended chan struct{}
	done  bool
}

type videoSubscriber struct {
	latest  chan *p.MediaFrame
	dropped atomic.Uint64
}

// subscribe attaches a socket to the reader for key, starting one when
// none is running, and returns the source and the subscriber's queue.
func (s *Server) subscribeVideo(key string, viewer *p.PlayerIdentity) (*videoRelaySource, *videoSubscriber) {
	sub := &videoSubscriber{latest: make(chan *p.MediaFrame, 1)}
	s.videoRelay.mu.Lock()
	defer s.videoRelay.mu.Unlock()
	if s.videoRelay.sources == nil {
		s.videoRelay.sources = map[string]*videoRelaySource{}
	}
	source := s.videoRelay.sources[key]
	if source == nil || source.done {
		source = &videoRelaySource{key: key, viewer: viewer, subscribers: map[*videoSubscriber]struct{}{}, ended: make(chan struct{})}
		s.videoRelay.sources[key] = source
		go s.runVideoSource(source)
	}
	source.subscribers[sub] = struct{}{}
	return source, sub
}

func (s *Server) unsubscribeVideo(source *videoRelaySource, sub *videoSubscriber) {
	s.videoRelay.mu.Lock()
	delete(source.subscribers, sub)
	s.videoRelay.mu.Unlock()
}

// idle ends the source when nobody subscribes to it any more; the check
// and the removal are one step so a socket joining meanwhile starts a
// fresh reader instead of attaching to one that is exiting.
func (s *Server) videoSourceIdle(source *videoRelaySource) bool {
	s.videoRelay.mu.Lock()
	defer s.videoRelay.mu.Unlock()
	if len(source.subscribers) > 0 {
		return false
	}
	source.done = true
	if s.videoRelay.sources[source.key] == source {
		delete(s.videoRelay.sources, source.key)
	}
	return true
}

// end marks the source's lease over: the reader exits and every
// subscriber, present or joining before the map entry is replaced, sees
// ended.
func (s *Server) videoSourceEnd(source *videoRelaySource) {
	s.videoRelay.mu.Lock()
	source.done = true
	if s.videoRelay.sources[source.key] == source {
		delete(s.videoRelay.sources, source.key)
	}
	close(source.ended)
	s.videoRelay.mu.Unlock()
}

// publish hands frame to every subscriber, replacing an undelivered
// older frame rather than waiting on a socket.
func (s *Server) publishVideo(source *videoRelaySource, frame *p.MediaFrame) {
	s.videoRelay.mu.Lock()
	defer s.videoRelay.mu.Unlock()
	for sub := range source.subscribers {
		select {
		case sub.latest <- frame:
		default:
			select {
			case <-sub.latest:
				sub.dropped.Add(1)
			default:
			}
			select {
			case sub.latest <- frame:
			default:
			}
		}
	}
}

// runVideoSource reads frames for one source until its lease ends or its
// last subscriber leaves. Frames come from ReadFrame until the first reply
// names the source; when Config.VideoFrames can open that source's
// shared-memory buffer, later frames are read from it directly and
// ReadFrame is only called once per videoSharedReconcile to confirm the
// lease is still alive and the source unchanged (a new lease publishes
// under a new name). Frames read from the buffer inherit the encoding and
// capture method of that ReadFrame reply, which the buffer header does not
// carry. AcknowledgeFrame is telemetry only, so only ReadFrame-delivered
// frames are acknowledged; shared-memory frames never cost a round trip.
func (s *Server) runVideoSource(source *videoRelaySource) {
	request := &p.FrameRequest{Viewer: source.viewer}
	if source.key != "" {
		request.SourceId = proto.String(source.key)
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
	ctx := context.Background()
	for range ticker.C {
		if s.videoSourceIdle(source) {
			return
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
				s.videoSourceEnd(source)
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
		s.publishVideo(source, frame)
		if !fromRPC {
			continue
		}
		ackCtx, cancelAck := context.WithTimeout(ctx, s.config.ReadTimeout)
		_, _, _ = s.config.PresentationMedia.AcknowledgeFrame(ackCtx, &p.FrameAcknowledgement{
			Viewer: source.viewer, Frame: frame.GetFrame(), DisplayedUnixMs: proto.Int64(time.Now().UnixMilli()),
		})
		cancelAck()
	}
}

// runVideoStream forwards the frames the source's shared reader publishes
// to one socket as binary messages (encodeVideoFrameMessage), newest
// first when the socket fell behind. It stops when the context is done
// (client disconnect or handler shutdown), the lease ends, or a write
// fails; a write that outlasts ReadTimeout is a stalled client and ends
// its stream alone.
func (s *Server) runVideoStream(ctx context.Context, conn *websocket.Conn, viewer *p.PlayerIdentity, wantSource string) {
	source, sub := s.subscribeVideo(wantSource, viewer)
	defer s.unsubscribeVideo(source, sub)
	for {
		select {
		case <-ctx.Done():
			return
		case <-source.ended:
			_ = conn.Close(websocket.StatusNormalClosure, "video lease ended")
			return
		case frame := <-sub.latest:
			writeCtx, cancelWrite := context.WithTimeout(ctx, s.config.ReadTimeout)
			err := conn.Write(writeCtx, websocket.MessageBinary, encodeVideoFrameMessage(frame))
			cancelWrite()
			if err != nil {
				return
			}
		}
	}
}
