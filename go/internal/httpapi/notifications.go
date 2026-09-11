package httpapi

import (
	"context"
	"errors"
	"net/http"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

func (s *Server) handleNotifications(w http.ResponseWriter, r *http.Request) bool {
	if r.URL.Path != "/api/presentation/notifications" {
		return false
	}
	if s.config.Notifications == nil {
		s.failure(w, r, 404, "not_found", "Notification reads are unavailable")
		return true
	}
	if r.Method != http.MethodGet {
		w.Header().Set("Allow", "GET")
		s.failure(w, r, 405, "method_not_allowed", "Notifications are read-only")
		return true
	}
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) != 0 || r.URL.RawQuery != "" || r.URL.ForceQuery {
		s.failure(w, r, 400, "invalid_request", "Notification reads require a bounded URL, no query and no body")
		return true
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	identity, err := s.presentationIdentity(ctx)
	if err != nil {
		s.readFailure(w, r, err)
		return true
	}
	request := &p.NotificationsRequest{Identity: &c.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}, IncludeLetters: proto.Bool(true), IncludeMessages: proto.Bool(true), IncludeAlerts: proto.Bool(true), LetterLimit: proto.Uint32(40), MessageLimit: proto.Uint32(12), AlertLimit: proto.Uint32(40)}
	reply, _, err := s.config.Notifications.ReadNotifications(ctx, request)
	observed := reply.GetNotifications()
	if err == nil && (observed == nil || (observed.GetLetters().GetObserved() == nil && observed.GetLetters().GetUnavailable() == nil) || (observed.GetMessages().GetObserved() == nil && observed.GetMessages().GetUnavailable() == nil) || (observed.GetAlerts().GetObserved() == nil && observed.GetAlerts().GetUnavailable() == nil)) {
		err = errors.New("requested notification section missing")
	}
	s.writePresentation(w, r, ctx, identity, reply, observed.GetContext(), err)
	return true
}
