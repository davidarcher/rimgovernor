// Package stepresult interprets failed MCP receipts for live errors and retained evidence.
package stepresult

import (
	"encoding/json"
	"strings"
)

// Failure separates a blocking GABS attention from a native exception or refusal.
// Detail retains the text blocks, including attention IDs and sample messages.
type Failure struct {
	Kind    string
	Summary string
	Detail  string
}

// Parse reads the result envelope, not a step's outer transport error. Successful
// receipts and malformed evidence are not classified as native refusals.
func Parse(raw json.RawMessage) (Failure, bool) {
	var result struct {
		IsError bool `json:"isError"`
		Content []struct {
			Text string `json:"text"`
		} `json:"content"`
		Structured struct {
			Exception   string `json:"exception"`
			AttentionID string `json:"attentionId"`
			Summary     string `json:"summary"`
		} `json:"structuredContent"`
	}
	if json.Unmarshal(raw, &result) != nil || !result.IsError {
		return Failure{}, false
	}
	var texts []string
	for _, block := range result.Content {
		if strings.TrimSpace(block.Text) != "" {
			texts = append(texts, block.Text)
		}
	}
	f := Failure{Kind: "refusal", Detail: strings.Join(texts, "\n")}
	f.Summary = firstLine(f.Detail)
	if result.Structured.Exception != "" {
		f.Kind = "native exception"
		f.Summary = firstLine(result.Structured.Exception)
	}
	lower := strings.ToLower(f.Detail)
	if result.Structured.AttentionID != "" || strings.Contains(lower, "blocking attention") || strings.Contains(lower, "attentionid:") || strings.Contains(lower, "attention id:") {
		f.Kind = "blocking attention"
		if result.Structured.Summary != "" {
			f.Summary = firstLine(result.Structured.Summary)
		}
		for _, line := range strings.Split(f.Detail, "\n") {
			if summary, ok := strings.CutPrefix(strings.TrimSpace(line), "Summary:"); ok {
				f.Summary = firstLine(summary)
				break
			}
		}
		// Preserve structured-only attention metadata (including sample messages).
		var envelope struct {
			Structured json.RawMessage `json:"structuredContent"`
		}
		_ = json.Unmarshal(raw, &envelope)
		if len(envelope.Structured) > 0 && string(envelope.Structured) != "null" {
			f.Detail += "\n" + string(envelope.Structured)
		}
	}
	if f.Summary == "" {
		f.Summary = "tool returned isError without a message"
	}
	return f, true
}

func firstLine(text string) string {
	line, _, _ := strings.Cut(strings.TrimSpace(text), "\n")
	return strings.TrimSpace(line)
}
