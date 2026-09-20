package nativeaccept

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// Only an explicit GABS refusal proves that retrying cannot repeat an executed order.
func blockingAttention(err error) bool {
	var refusal *bridge.Refusal
	if !errors.As(err, &refusal) {
		return false
	}
	var body struct {
		Status string `json:"status"`
	}
	return json.Unmarshal(refusal.Result.Structured, &body) == nil && body.Status == "blocked_by_attention"
}

// Attention receipts are evidence, not a whitelist of harmless exceptions. Keep
// every sample and severity even when acknowledgement lets the case continue.
func (h *Harness) readAndAckAttention(ctx context.Context, record func(map[string]any)) (map[string]any, error) {
	result, err := h.Client.GetAttention(ctx)
	row := map[string]any{"result": result.Envelope}
	record(row)
	fail := func(err error) (map[string]any, error) {
		row["error"] = err.Error()
		return row, err
	}
	if err != nil {
		return fail(fmt.Errorf("games_get_attention: %w", err))
	}
	var body struct {
		Attention *struct {
			ID string `json:"attentionId"`
		} `json:"attention"`
	}
	if err := json.Unmarshal(result.Structured, &body); err != nil {
		return fail(fmt.Errorf("games_get_attention: invalid reply: %w", err))
	}
	if body.Attention == nil {
		return row, nil
	}
	if body.Attention.ID == "" {
		return fail(fmt.Errorf("games_get_attention: attention missing attentionId"))
	}
	ack, err := h.Client.AckAttention(ctx, body.Attention.ID)
	row["acknowledgement"] = ack.Envelope
	if err != nil {
		return fail(fmt.Errorf("games_ack_attention: %w", err))
	}
	row["acknowledged"] = true
	return row, nil
}

// Aggregate from steps so reports also retain attentions from short-lived or
// standalone harnesses, including a run that failed during session startup.
func (r Report) collectAttentions(output string) {
	paths, _ := filepath.Glob(filepath.Join(output, "[0-9]*-*.json"))
	var attentions []map[string]any
	for _, path := range paths {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		var step struct {
			Attention map[string]any `json:"attention"`
		}
		if json.Unmarshal(data, &step) != nil || step.Attention == nil {
			continue
		}
		step.Attention["step"] = filepath.Base(path)
		attentions = append(attentions, step.Attention)
	}
	if len(attentions) > 0 {
		r["attentions"] = attentions
	}
}
