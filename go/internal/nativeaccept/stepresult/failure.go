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
//
// A refusal does not have to arrive as an MCP error. A fixture op that
// declines reports a successful receipt carrying "success": false and its
// own reason, and a fixture whose game threw reports the exception the same
// way; requiring isError left both as a bare "bridge read refused:
// games_call_tool" with the cause only in the evidence tree (#663). The gate
// here matches the one the bridge itself refuses on.
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
			Reason      string `json:"reason"`
			Success     *bool  `json:"success"`
			Refused     bool   `json:"refused"`
		} `json:"structuredContent"`
	}
	if json.Unmarshal(raw, &result) != nil {
		return Failure{}, false
	}
	// An exception alone is not a failure: a successful reply may report one
	// it handled. The refusal flags are what the bridge itself refuses on.
	declined := result.Structured.Success != nil && !*result.Structured.Success
	if !result.IsError && !declined && !result.Structured.Refused {
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
	// A declined op's single text block is the whole reply JSON, which says
	// nothing on one line; its structured "reason" is the refusal itself.
	if result.Structured.Reason != "" {
		f.Summary = firstLine(result.Structured.Reason)
	}
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

// firstLine is text's first line. A managed stack trace reaches us with its
// own backslashes escaped to forward slashes, so the line separators arrive
// as the literal "/r/n" rather than as newlines; both end the line, or the
// summary carries the whole trace.
func firstLine(text string) string {
	line := strings.TrimSpace(text)
	for _, separator := range []string{"\n", "/r/n", "/n", "/r"} {
		line, _, _ = strings.Cut(line, separator)
	}
	return strings.TrimSpace(line)
}
