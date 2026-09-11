package model

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"unicode/utf8"
)

type envelope struct {
	Choices []choice        `json:"choices"`
	Usage   *Usage          `json:"usage"`
	Error   json.RawMessage `json:"error"`
}
type choice struct {
	Index        *int             `json:"index"`
	Message      *responseMessage `json:"message"`
	Delta        *responseMessage `json:"delta"`
	FinishReason *string          `json:"finish_reason"`
}
type responseMessage struct {
	Role         *string         `json:"role"`
	Content      *string         `json:"content"`
	Refusal      *string         `json:"refusal"`
	ToolCalls    json.RawMessage `json:"tool_calls"`
	FunctionCall json.RawMessage `json:"function_call"`
}

func decodeEnvelope(data []byte) (envelope, error) {
	var value envelope
	if !utf8.Valid(data) {
		return value, fmt.Errorf("%w: invalid UTF-8", ErrInvalidResponse)
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err := uniqueJSON(decoder, 0); err != nil {
		return value, fmt.Errorf("%w: %v", ErrInvalidResponse, err)
	}
	if _, err := decoder.Token(); err != io.EOF {
		return value, fmt.Errorf("%w: trailing JSON", ErrInvalidResponse)
	}
	if err := json.Unmarshal(data, &value); err != nil {
		return value, fmt.Errorf("%w: %v", ErrInvalidResponse, err)
	}
	if present(value.Error) {
		return value, fmt.Errorf("%w: %s", ErrInvalidResponse, serverErrorMessage(data))
	}
	if value.Usage != nil {
		for _, count := range []*int64{value.Usage.PromptTokens, value.Usage.CompletionTokens, value.Usage.TotalTokens} {
			if count != nil && *count < 0 {
				return value, fmt.Errorf("%w: negative token usage", ErrInvalidResponse)
			}
		}
	}
	return value, nil
}

func uniqueJSON(decoder *json.Decoder, depth int) error {
	if depth > 64 {
		return fmt.Errorf("JSON nesting exceeds limit")
	}
	value, err := decoder.Token()
	if err != nil {
		return err
	}
	delim, ok := value.(json.Delim)
	if !ok {
		return nil
	}
	if delim != '{' && delim != '[' {
		return fmt.Errorf("invalid delimiter")
	}
	seen := map[string]bool{}
	for decoder.More() {
		if delim == '{' {
			value, err := decoder.Token()
			if err != nil {
				return err
			}
			key, ok := value.(string)
			if !ok || seen[key] {
				return fmt.Errorf("duplicate JSON key")
			}
			seen[key] = true
		}
		if err := uniqueJSON(decoder, depth+1); err != nil {
			return err
		}
	}
	_, err = decoder.Token()
	return err
}

func present(data json.RawMessage) bool {
	return len(data) > 0 && !bytes.Equal(bytes.TrimSpace(data), []byte("null"))
}

func messageText(message *responseMessage, requireRole bool) (string, error) {
	if message == nil {
		return "", fmt.Errorf("%w: missing message", ErrInvalidResponse)
	}
	if requireRole && message.Role == nil || message.Role != nil && *message.Role != "assistant" {
		return "", fmt.Errorf("%w: expected assistant role", ErrInvalidResponse)
	}
	if message.Refusal != nil && *message.Refusal != "" {
		return "", ErrRefusal
	}
	if present(message.FunctionCall) || present(message.ToolCalls) && !bytes.Equal(bytes.TrimSpace(message.ToolCalls), []byte("[]")) {
		return "", ErrToolCalls
	}
	if message.Content == nil {
		return "", nil
	}
	return *message.Content, nil
}

func finish(reason *string) (FinishReason, error) {
	if reason == nil {
		return "", fmt.Errorf("%w: missing finish reason", ErrInvalidResponse)
	}
	switch *reason {
	case "stop":
		return Stop, nil
	case "length":
		return Length, ErrOutputLimit
	case "tool_calls", "function_call":
		return "", ErrToolCalls
	case "content_filter":
		return "", ErrRefusal
	default:
		return "", fmt.Errorf("%w: unsupported finish reason", ErrInvalidResponse)
	}
}

func decodeCompletion(data []byte) (Response, error) {
	var result Response
	value, err := decodeEnvelope(data)
	if err != nil {
		return result, err
	}
	if len(value.Choices) != 1 || value.Choices[0].Index == nil || *value.Choices[0].Index != 0 {
		return result, fmt.Errorf("%w: expected one choice at index zero", ErrInvalidResponse)
	}
	selected := value.Choices[0]
	result.Text, err = messageText(selected.Message, true)
	if err != nil {
		return Response{}, err
	}
	if selected.Message.Content == nil {
		return Response{}, fmt.Errorf("%w: missing text content", ErrInvalidResponse)
	}
	result.Usage = value.Usage
	result.FinishReason, err = finish(selected.FinishReason)
	return result, err
}

func serverErrorMessage(data []byte) string {
	var value struct {
		Error json.RawMessage `json:"error"`
	}
	if json.Unmarshal(data, &value) != nil {
		return "server returned an unsuccessful response"
	}
	var message string
	if json.Unmarshal(value.Error, &message) != nil {
		var detail struct {
			Message string `json:"message"`
		}
		if json.Unmarshal(value.Error, &detail) == nil {
			message = detail.Message
		}
	}
	message = strings.TrimSpace(message)
	if len(message) > 800 {
		message = message[:800]
	}
	if message == "" {
		message = "server returned an unsuccessful response"
	}
	return message
}
