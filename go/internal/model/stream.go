package model

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"strings"
)

// Frames are assembled across arbitrary network fragments. Their raw wire size
// includes comments and fields; the response limit also counts every raw byte.
func (client *Client) readStream(ctx context.Context, body io.Reader, onText func(string) error) (Response, error) {
	var result Response
	reader := bufio.NewReader(io.LimitReader(body, client.config.MaxResponseBytes+1))
	var line []byte
	var data []string
	var text strings.Builder
	var total, frame int64
	finished, previousCR, pendingBoundary := false, false, false
	for {
		if err := ctx.Err(); err != nil {
			return result, err
		}
		value, err := reader.ReadByte()
		if err != nil {
			if err == io.EOF {
				return result, fmt.Errorf("%w: stream ended before [DONE]", ErrInvalidResponse)
			}
			return result, err
		}
		total++
		if total > client.config.MaxResponseBytes {
			return result, ErrResponseTooLarge
		}
		if pendingBoundary {
			pendingBoundary = false
			if previousCR && value == '\n' {
				if frame+1 > client.config.MaxFrameBytes {
					return result, ErrFrameTooLarge
				}
				frame = 0
				previousCR = false
				continue
			}
			frame = 0
		}
		frame++
		if frame > client.config.MaxFrameBytes {
			return result, ErrFrameTooLarge
		}
		if previousCR && value == '\n' {
			previousCR = false
			continue
		}
		previousCR = value == '\r'
		if value != '\r' && value != '\n' {
			line = append(line, value)
			continue
		}
		if len(line) != 0 {
			name, content, _ := strings.Cut(string(line), ":")
			if name == "data" {
				data = append(data, strings.TrimPrefix(content, " "))
			}
			line = line[:0]
			continue
		}
		pendingBoundary = true
		if len(data) == 0 {
			continue
		}
		raw := strings.Join(data, "\n")
		data = nil
		if raw == "[DONE]" {
			if !finished {
				return result, fmt.Errorf("%w: [DONE] before finish reason", ErrInvalidResponse)
			}
			if result.FinishReason == Length {
				return result, ErrOutputLimit
			}
			return result, nil
		}
		chunk, err := decodeEnvelope([]byte(raw))
		if err != nil {
			return result, err
		}
		if chunk.Usage != nil {
			result.Usage = chunk.Usage
		}
		if len(chunk.Choices) == 0 && chunk.Usage != nil {
			continue
		}
		if finished || len(chunk.Choices) != 1 || chunk.Choices[0].Index == nil || *chunk.Choices[0].Index != 0 {
			return result, fmt.Errorf("%w: expected one unfinished choice at index zero", ErrInvalidResponse)
		}
		selected := chunk.Choices[0]
		delta, err := messageText(selected.Delta, false)
		if err != nil {
			return result, err
		}
		if selected.FinishReason != nil {
			reason, err := finish(selected.FinishReason)
			if err != nil && !errors.Is(err, ErrOutputLimit) {
				return result, err
			}
			result.FinishReason = reason
			finished = true
		}
		if int64(text.Len()+len(delta)) > client.config.MaxResponseBytes {
			return result, ErrResponseTooLarge
		}
		text.WriteString(delta)
		result.Text = text.String()
		if delta != "" {
			if err := onText(delta); err != nil {
				return result, err
			}
			if err := ctx.Err(); err != nil {
				return result, err
			}
		}
	}
}
