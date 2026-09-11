// Package model provides text-only transport to an explicitly configured local
// model server. It has no plan, tool execution, storage, or provider fallback API.
package model

import (
	"errors"
	"fmt"
	"time"
)

type Role string

const (
	System    Role = "system"
	User      Role = "user"
	Assistant Role = "assistant"
)

type Message struct {
	Role    Role   `json:"role"`
	Content string `json:"content"`
}

type Request struct {
	Messages        []Message
	MaxOutputTokens int
}

type FinishReason string

const (
	Stop   FinishReason = "stop"
	Length FinishReason = "length"
)

// Unknown usage remains nil, including individual counts omitted by the server.
// No character/byte count is reported as a token count.
type Usage struct {
	PromptTokens     *int64 `json:"prompt_tokens"`
	CompletionTokens *int64 `json:"completion_tokens"`
	TotalTokens      *int64 `json:"total_tokens"`
}

type Response struct {
	Text         string
	FinishReason FinishReason
	Usage        *Usage
}

type Config struct {
	Model            string
	BaseURL          string
	Timeout          time.Duration
	MaxResponseBytes int64
	// Zero selects min(64 KiB, MaxResponseBytes).
	MaxFrameBytes   int64
	AllowDockerHost bool
}

var (
	ErrClosed           = errors.New("local model client is closed")
	ErrInvalidResponse  = errors.New("invalid local model response")
	ErrResponseTooLarge = errors.New("local model response exceeds byte limit")
	ErrFrameTooLarge    = errors.New("local model stream frame exceeds byte limit")
	ErrOutputLimit      = errors.New("local model output token limit reached")
	ErrToolCalls        = errors.New("tool calls are unsupported by text transport")
	ErrRefusal          = errors.New("local model refused the request")
)

type HTTPError struct {
	StatusCode int
	Message    string
}

func (err *HTTPError) Error() string {
	if err.Message == "" {
		return fmt.Sprintf("local model HTTP %d", err.StatusCode)
	}
	return fmt.Sprintf("local model HTTP %d: %s", err.StatusCode, err.Message)
}
