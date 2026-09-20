package bridge

import (
	"fmt"
	"unicode/utf16"
	"unicode/utf8"
)

// asciiJSON rewrites every non-ASCII character of a JSON text as a \uXXXX
// escape (a surrogate pair above the BMP), leaving the value unchanged for
// any JSON parser. The frame gabs writes to the game carries this text as a
// string value, and the host's GABP reader (Lib.GAB TcpTransport 2.1.1)
// counts Content-Length in bytes but slices the decoded frame in chars:
// one multi-byte character short-reads the frame, and every call after it
// on that connection times out (#600). The escape survives gabs because
// the text is a string value there, re-encoded with its backslashes
// intact; a non-ASCII value in a plain argument map does not, so legacy
// map-argument tools must stay ASCII. ProtoJSON never emits non-ASCII
// outside a string, so the whole text can be walked without parsing.
func asciiJSON(text []byte) []byte {
	ascii := true
	for _, b := range text {
		if b >= utf8.RuneSelf {
			ascii = false
			break
		}
	}
	if ascii {
		return text
	}
	out := make([]byte, 0, len(text)+16)
	for len(text) > 0 {
		r, size := utf8.DecodeRune(text)
		text = text[size:]
		if r < utf8.RuneSelf {
			out = append(out, byte(r))
			continue
		}
		if r >= 0x10000 {
			hi, lo := utf16.EncodeRune(r)
			out = append(out, fmt.Sprintf(`\u%04x\u%04x`, hi, lo)...)
			continue
		}
		out = append(out, fmt.Sprintf(`\u%04x`, r)...)
	}
	return out
}
