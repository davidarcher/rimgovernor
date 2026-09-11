package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func (s *Server) handlePresentation(w http.ResponseWriter, r *http.Request) bool {
	switch r.URL.Path {
	case "/api/presentation/camera", "/api/presentation/selection", "/api/presentation/colonists":
	default:
		return false
	}
	if s.config.Presentation == nil {
		s.failure(w, r, 404, "not_found", "Presentation reads are unavailable")
		return true
	}
	if r.Method != http.MethodGet {
		w.Header().Set("Allow", "GET")
		s.failure(w, r, 405, "method_not_allowed", "Presentation is read-only")
		return true
	}
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) != 0 || r.URL.RawQuery != "" || r.URL.ForceQuery {
		s.failure(w, r, 400, "invalid_request", "Presentation reads require a bounded URL, no query and no body")
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
	var reply proto.Message
	var observed *c.ObservationContext
	switch r.URL.Path {
	case "/api/presentation/camera":
		value, _, cause := s.config.Presentation.ReadCamera(ctx, &p.ReadRequest{Identity: proto.Clone(wire).(*c.Identity)})
		err = cause
		reply = value
		observed = value.GetCamera().GetContext()
	case "/api/presentation/selection":
		value, _, cause := s.config.Presentation.ReadSelection(ctx, &p.ReadRequest{Identity: proto.Clone(wire).(*c.Identity)})
		err = cause
		reply = value
		observed = value.GetSelection().GetContext()
	case "/api/presentation/colonists":
		value, _, cause := s.config.Presentation.ReadColonistRoster(ctx, &p.ColonistRosterRequest{Identity: proto.Clone(wire).(*c.Identity), CurrentMapOnly: proto.Bool(true)})
		err = cause
		reply = value
		observed = value.GetRoster().GetContext()
	}
	s.writePresentation(w, r, ctx, identity, reply, observed, err)
	return true
}

func (s *Server) writePresentation(w http.ResponseWriter, r *http.Request, ctx context.Context, identity observation.Identity, reply proto.Message, observed *c.ObservationContext, err error) {
	wire := &c.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}
	if err == nil {
		err = ctx.Err()
	}
	if err == nil {
		err = bridge.ValidateContext(observed)
	}
	if err == nil && (!proto.Equal(observed.Identity, wire) || observed.GetTick() < int64(identity.Tick)) {
		err = errors.New("presentation context changed")
	}
	if err == nil {
		latest, cause := s.presentationIdentity(ctx)
		err = cause
		if err == nil && (latest.Colony != identity.Colony || latest.Map != identity.Map || latest.Load != identity.Load) {
			err = errors.New("presentation world changed")
		}
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	// The provider validates native domain facts. Reject unrepresentable values
	// before ProtoJSON can silently discard unknown binary fields or stringify NaN.
	if !presentationWire(reply.ProtoReflect()) {
		s.readFailure(w, r, errors.New("invalid presentation wire"))
		return
	}
	if proto.Size(reply) > min(s.config.MaxResponseBytes, 1<<20) {
		s.failure(w, r, 503, "response_limit", "Presentation response exceeds its configured bound")
		return
	}
	payload, err := protojson.Marshal(reply)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, json.RawMessage(payload))
	return
}
func (s *Server) presentationIdentity(ctx context.Context) (observation.Identity, error) {
	if err := ctx.Err(); err != nil {
		return observation.Identity{}, err
	}
	snapshot, err := s.snapshots.Snapshot(ctx)
	if err != nil {
		return observation.Identity{}, err
	}
	if err = ctx.Err(); err != nil {
		return observation.Identity{}, err
	}
	identity, known := snapshot.Identity.Value()
	if !snapshot.Connected || snapshot.Stale || !known {
		return observation.Identity{}, errors.New("fresh identity unavailable")
	}
	return identity, identity.Validate()
}
func presentationWire(message protoreflect.Message) bool {
	if !message.IsValid() || len(message.GetUnknown()) != 0 {
		return false
	}
	valid := true
	message.Range(func(field protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		check := func(value protoreflect.Value) bool {
			switch field.Kind() {
			case protoreflect.MessageKind:
				return presentationWire(value.Message())
			case protoreflect.FloatKind, protoreflect.DoubleKind:
				return !math.IsNaN(value.Float()) && !math.IsInf(value.Float(), 0)
			}
			return true
		}
		if field.IsList() {
			list := value.List()
			for i := 0; i < list.Len(); i++ {
				if !check(list.Get(i)) {
					valid = false
					return false
				}
			}
		} else {
			valid = check(value)
		}
		return valid
	})
	return valid
}
