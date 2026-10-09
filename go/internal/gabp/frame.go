// Package gabp speaks the GABP wire protocol (gabp/1) directly to the
// RimBridgeServer mod: LSP-style Content-Length frames over TCP loopback,
// a session/hello handshake, then concurrent tools/list and tools/call
// requests.
package gabp

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"
)

// ErrFrameTooLarge reports an incoming frame whose Content-Length exceeds
// the reader's cap. The stream cannot be resynchronised after it.
var ErrFrameTooLarge = errors.New("gabp: frame exceeds size cap")

// ASCIIJSON rewrites every non-ASCII character of a JSON text as a \uXXXX
// escape (a surrogate pair above the BMP), leaving the value unchanged for
// any JSON parser. RimBridgeServer's reader (Lib.GAB TcpTransport) counts
// Content-Length in bytes but slices the decoded frame in chars, so one
// multi-byte character short-reads the frame and wedges every later call on
// the connection. Valid JSON never carries non-ASCII outside a
// string, so the text is walked without parsing.
func ASCIIJSON(text []byte) []byte {
	i := 0
	for i < len(text) && text[i] < utf8.RuneSelf {
		i++
	}
	if i == len(text) {
		return text
	}
	out := make([]byte, 0, len(text)+16)
	out = append(out, text[:i]...)
	for text = text[i:]; len(text) > 0; {
		r, size := utf8.DecodeRune(text)
		text = text[size:]
		switch {
		case r < utf8.RuneSelf:
			out = append(out, byte(r))
		case r >= 0x10000:
			hi, lo := utf16.EncodeRune(r)
			out = fmt.Appendf(out, `\u%04x\u%04x`, hi, lo)
		default:
			out = fmt.Appendf(out, `\u%04x`, r)
		}
	}
	return out
}

// WriteFrame writes body as one Content-Length frame in a single Write.
func WriteFrame(w io.Writer, body []byte) error {
	frame := make([]byte, 0, len(body)+32)
	frame = fmt.Appendf(frame, "Content-Length: %d\r\n\r\n", len(body))
	frame = append(frame, body...)
	_, err := w.Write(frame)
	return err
}

// ReadFrame reads one Content-Length frame. maxBytes <= 0 means no cap.
// Header lines other than Content-Length are ignored.
func ReadFrame(r *bufio.Reader, maxBytes int) ([]byte, error) {
	length := -1
	for {
		line, err := r.ReadString('\n')
		if err != nil {
			if err == io.EOF && line != "" {
				err = io.ErrUnexpectedEOF
			}
			return nil, err
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			if length < 0 {
				continue // tolerate stray blank lines between frames
			}
			break
		}
		name, value, ok := strings.Cut(line, ":")
		if !ok || !strings.EqualFold(strings.TrimSpace(name), "Content-Length") {
			continue
		}
		n, err := strconv.Atoi(strings.TrimSpace(value))
		if err != nil || n < 0 {
			return nil, fmt.Errorf("gabp: bad Content-Length %q", value)
		}
		length = n
	}
	if maxBytes > 0 && length > maxBytes {
		return nil, fmt.Errorf("%w: %d > %d bytes", ErrFrameTooLarge, length, maxBytes)
	}
	body := make([]byte, length)
	if _, err := io.ReadFull(r, body); err != nil {
		if err == io.EOF {
			err = io.ErrUnexpectedEOF
		}
		return nil, err
	}
	return body, nil
}
