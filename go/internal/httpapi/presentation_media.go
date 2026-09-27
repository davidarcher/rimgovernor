package httpapi

import (
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net/http"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// RenderStatusDTO mirrors rimgovernor.presentation.v1.RenderStatus for the two
// player-facing media mutations. It carries no ObservationContext: the caller
// already knows its own identity, and the native context is re-derived and
// re-validated against it before every response (see writePresentation's
// sibling checks in handlePresentation).
type RenderStatusDTO struct {
	Supported        bool   `json:"supported"`
	Suspended        bool   `json:"suspended"`
	WindowVisible    bool   `json:"windowVisible"`
	RemainingLeaseMs uint32 `json:"remainingLeaseMs"`
}
type renderDemandRequestDTO struct {
	LeaseSeconds *uint32 `json:"leaseSeconds"`
}

// handlePresentationMedia serves PresentationMedia.DemandRendering under the same X-RimGovernor-Player gate as
// other player writes in player.go. RenderState itself stays a free read,
// exposed by handlePresentation; only a lease change requires the player
// token.
func (s *Server) handlePresentationMedia(w http.ResponseWriter, r *http.Request) bool {
	switch r.URL.Path {
	case "/api/presentation/render-demand":
	default:
		return false
	}
	if s.config.PresentationMedia == nil || s.playerToken == "" {
		s.failure(w, r, 404, "not_found", "Presentation media is unavailable")
		return true
	}
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", "POST")
		s.failure(w, r, 405, "method_not_allowed", "Presentation media requires POST")
		return true
	}
	tokens := r.Header.Values("X-RimGovernor-Player")
	if len(tokens) != 1 || subtle.ConstantTimeCompare([]byte(tokens[0]), []byte(s.playerToken)) != 1 {
		s.failure(w, r, 403, "player_auth", "Player session token required")
		return true
	}
	kind, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || kind != "application/json" {
		s.failure(w, r, 415, "content_type", "Use application/json")
		return true
	}
	if r.URL.RawQuery != "" || r.URL.ForceQuery || r.ContentLength > buildingRequestLimit {
		s.failure(w, r, 400, "invalid_request", "Mutation requires a bounded body and no query")
		return true
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	identity, err := s.presentationIdentity(ctx)
	if err != nil {
		s.readFailure(w, r, err)
		return true
	}
	wire := &c.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}
	s.handleDemandRendering(w, r, ctx, wire)
	return true
}

func decodeMediaBody(r *http.Request, out any) error {
	data, err := io.ReadAll(io.LimitReader(r.Body, buildingRequestLimit+1))
	if err != nil {
		return err
	}
	if len(data) > buildingRequestLimit {
		return errors.New("request exceeds byte limit")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err = decoder.Decode(out); err != nil {
		return err
	}
	if decoder.More() {
		return errors.New("trailing JSON")
	}
	return nil
}

func (s *Server) handleDemandRendering(w http.ResponseWriter, r *http.Request, ctx context.Context, wire *c.Identity) {
	var body renderDemandRequestDTO
	if err := decodeMediaBody(r, &body); err != nil || body.LeaseSeconds == nil || *body.LeaseSeconds > 30 {
		s.failure(w, r, 400, "invalid_request", "leaseSeconds (0-30) is required")
		return
	}
	if err := ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	request := &p.RenderDemand{Viewer: &p.PlayerIdentity{Identity: wire}, LeaseSeconds: proto.Uint32(*body.LeaseSeconds)}
	reply, _, err := s.config.PresentationMedia.DemandRendering(ctx, request)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	status := reply.GetStatus()
	s.write(w, r, 200, RenderStatusDTO{status.GetSupported(), status.GetSuspended(), status.GetWindowVisible(), status.GetRemainingLeaseMs()})
}
