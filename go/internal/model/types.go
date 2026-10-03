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

type Response struct {
	Text         string
	FinishReason FinishReason
}

type Config struct {
	Model            string
	BaseURL          string
	Timeout          time.Duration
	MaxResponseBytes int64
}

var (
	ErrClosed           = errors.New("local model client is closed")
	ErrInvalidResponse  = errors.New("invalid local model response")
	ErrResponseTooLarge = errors.New("local model response exceeds byte limit")
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
