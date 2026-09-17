package nativeaccept

import (
	"bytes"
	"encoding/xml"
	"fmt"
	"io"
)

// xmlItem is one ordered child of an xmlElem: either an element or literal text.
// RimWorld's config XML (ModsConfig.xml) mixes indentation whitespace with elements,
// so a plain encoding/xml struct mapping cannot round-trip it losslessly; this minimal
// mutable tree keeps elements and whitespace in document order.
type xmlItem struct {
	elem *xmlElem
	text string
}

type xmlElem struct {
	name  xml.Name
	attrs []xml.Attr
	kids  []xmlItem
}

func (e *xmlElem) find(name string) *xmlElem {
	if e == nil {
		return nil
	}
	for _, kid := range e.kids {
		if kid.elem != nil && kid.elem.name.Local == name {
			return kid.elem
		}
	}
	return nil
}

func (e *xmlElem) childText(name string) string {
	child := e.find(name)
	if child == nil {
		return ""
	}
	var out bytes.Buffer
	for _, kid := range child.kids {
		if kid.elem == nil {
			out.WriteString(kid.text)
		}
	}
	return out.String()
}

// findAll returns every direct child element named name, in document order.
func (e *xmlElem) findAll(name string) []*xmlElem {
	var out []*xmlElem
	if e == nil {
		return out
	}
	for _, kid := range e.kids {
		if kid.elem != nil && kid.elem.name.Local == name {
			out = append(out, kid.elem)
		}
	}
	return out
}

// children returns every direct child element regardless of name, in document order.
func (e *xmlElem) children() []*xmlElem {
	var out []*xmlElem
	if e == nil {
		return out
	}
	for _, kid := range e.kids {
		if kid.elem != nil {
			out = append(out, kid.elem)
		}
	}
	return out
}

// attr returns the named attribute's value, or "" if absent.
func (e *xmlElem) attr(name string) string {
	if e == nil {
		return ""
	}
	for _, a := range e.attrs {
		if a.Name.Local == name {
			return a.Value
		}
	}
	return ""
}

func (e *xmlElem) text() string {
	var out bytes.Buffer
	for _, kid := range e.kids {
		if kid.elem == nil {
			out.WriteString(kid.text)
		}
	}
	return out.String()
}

func parseXML(data []byte) (header string, root *xmlElem, err error) {
	decoder := xml.NewDecoder(bytes.NewReader(data))
	// RimWorld writes config XML with encoding='utf8' (no hyphen), which the Go
	// stdlib does not recognize as an alias for UTF-8. The document bytes are
	// already UTF-8, so pass them through unchanged rather than transcoding.
	decoder.CharsetReader = func(charset string, input io.Reader) (io.Reader, error) {
		return input, nil
	}
	var stack []*xmlElem
	seenRoot := false
	for {
		token, tokErr := decoder.Token()
		if tokErr == io.EOF {
			break
		}
		if tokErr != nil {
			return "", nil, fmt.Errorf("parse xml: %w", tokErr)
		}
		switch t := token.(type) {
		case xml.ProcInst:
			if !seenRoot {
				header = fmt.Sprintf("<?%s %s?>", t.Target, string(t.Inst))
			}
		case xml.StartElement:
			seenRoot = true
			node := &xmlElem{name: t.Name, attrs: append([]xml.Attr(nil), t.Attr...)}
			if len(stack) > 0 {
				parent := stack[len(stack)-1]
				parent.kids = append(parent.kids, xmlItem{elem: node})
			}
			stack = append(stack, node)
		case xml.EndElement:
			if len(stack) == 0 {
				return "", nil, fmt.Errorf("parse xml: unbalanced end element %s", t.Name.Local)
			}
			finished := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			if len(stack) == 0 {
				root = finished
			}
		case xml.CharData:
			if len(stack) > 0 {
				top := stack[len(stack)-1]
				top.kids = append(top.kids, xmlItem{text: string(t)})
			}
		}
	}
	if root == nil {
		return "", nil, fmt.Errorf("parse xml: no root element")
	}
	return header, root, nil
}

func writeXML(header string, root *xmlElem) ([]byte, error) {
	var out bytes.Buffer
	if header != "" {
		out.WriteString(header)
	}
	if err := writeElem(&out, root); err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}

func writeElem(out *bytes.Buffer, e *xmlElem) error {
	out.WriteByte('<')
	out.WriteString(e.name.Local)
	for _, attr := range e.attrs {
		out.WriteByte(' ')
		out.WriteString(attr.Name.Local)
		out.WriteString(`="`)
		if err := xml.EscapeText(out, []byte(attr.Value)); err != nil {
			return err
		}
		out.WriteByte('"')
	}
	if len(e.kids) == 0 {
		out.WriteString("></" + e.name.Local + ">")
		return nil
	}
	out.WriteByte('>')
	for _, kid := range e.kids {
		if kid.elem != nil {
			if err := writeElem(out, kid.elem); err != nil {
				return err
			}
			continue
		}
		if err := xml.EscapeText(out, []byte(kid.text)); err != nil {
			return err
		}
	}
	out.WriteString("</" + e.name.Local + ">")
	return nil
}
