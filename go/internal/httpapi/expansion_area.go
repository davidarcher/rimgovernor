package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type playerExpansionArea interface {
	ChangeExpansionArea(context.Context, store.World, string, string, []domain.Cell, bool) error
}

func (s *Server) handleExpansionArea(ctx context.Context, w http.ResponseWriter, r *http.Request) {
	player, ok := s.player.(playerExpansionArea)
	if !ok {
		s.failure(w, r, 404, "not_found", "Expansion areas are not enabled")
		return
	}
	remove := strings.HasSuffix(r.URL.Path, "/remove")
	keys := []string{"expected", "id", "reason"}
	if !remove {
		keys = append(keys, "cells")
	}
	f, err := buildingRequest(r.Body, keys...)
	var world store.World
	var id, reason string
	var cells []domain.Cell
	if err == nil {
		world, err = buildingWorld(f["expected"])
	}
	if err == nil {
		err = json.Unmarshal(f["id"], &id)
	}
	if err == nil {
		err = json.Unmarshal(f["reason"], &reason)
	}
	if err == nil && !remove {
		var rows []json.RawMessage
		err = json.Unmarshal(f["cells"], &rows)
		seen := map[domain.Cell]bool{}
		for _, raw := range rows {
			var cell domain.Cell
			var fields map[string]json.RawMessage
			if err == nil {
				fields, err = buildingFields(raw, "x", "z")
			}
			if err == nil {
				err = json.Unmarshal(fields["x"], &cell.X)
			}
			if err == nil {
				err = json.Unmarshal(fields["z"], &cell.Z)
			}
			if err != nil {
				break
			}
			if cell.X < 0 || cell.Z < 0 || seen[cell] {
				err = errors.New("invalid expansion cell")
				break
			}
			seen[cell] = true
			cells = append(cells, cell)
		}
	}
	if err != nil || buildingRequestID(id) != nil || strings.TrimSpace(reason) == "" || len(reason) > 1024 || !remove && (len(cells) == 0 || len(cells) > 65536) {
		s.failure(w, r, 400, "invalid_request", "Provide expected world, area id, reason and bounded cells for an addition")
		return
	}
	if err = player.ChangeExpansionArea(ctx, world, id, reason, cells, remove); err != nil {
		status, failure := playerFailure(err)
		s.failure(w, r, status, failure.Code, failure.Detail)
		return
	}
	s.write(w, r, 200, struct {
		ID      string `json:"id"`
		Removed bool   `json:"removed"`
	}{id, remove})
}
