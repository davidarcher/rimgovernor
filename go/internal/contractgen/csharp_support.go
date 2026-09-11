package contractgen

// csharpBoundary is emitted per root type. Newtonsoft consumes tokens only after
// this scanner verifies strict JSON grammar and Unicode scalar strings.
const csharpBoundary = `
    internal static class __BOUNDARY__
    {
        internal static JToken Parse(string json)
        {
            if (json == null) throw new FormatException("Missing JSON input");
            try
            {
                if (new System.Text.UTF8Encoding(false, true).GetByteCount(json) > 1048576)
                    throw new FormatException("JSON input exceeds 1 MiB");
            }
            catch (System.Text.EncoderFallbackException error)
            {
                throw new FormatException("JSON contains an invalid Unicode scalar", error);
            }
            new Scanner(json).Validate();
            try
            {
                using (var source = new System.IO.StringReader(json))
                using (var reader = new JsonTextReader(source))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Double;
                    reader.MaxDepth = 65;
                    var token = JToken.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        LineInfoHandling = LineInfoHandling.Ignore
                    });
                    if (reader.Read()) throw new FormatException("Trailing JSON data");
                    return token;
                }
            }
            catch (JsonException error)
            {
                throw new FormatException("Invalid JSON token stream", error);
            }
        }

        internal static string ReadString(JToken token, int maximum, bool nonblank)
        {
            if (token.Type != JTokenType.String) throw new FormatException("Expected string");
            string value = token.Value<string>()!;
            if (value.Length > maximum) throw new FormatException("String exceeds UTF-16 limit");
            if (nonblank && IsBlank(value)) throw new FormatException("Blank string is forbidden");
            return value;
        }

        private static bool IsBlank(string value)
        {
            foreach (char c in value)
                if (!(c >= '\u0009' && c <= '\u000D') && c != '\u0020' && c != '\u0085' && c != '\u00A0'
                    && c != '\u1680' && !(c >= '\u2000' && c <= '\u200A') && c != '\u2028' && c != '\u2029'
                    && c != '\u202F' && c != '\u205F' && c != '\u3000') return false;
            return true;
        }

        internal static int ReadInteger(JToken token, int minimum, int maximum)
        {
            int value;
            if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw new FormatException("Expected bounded int32 integer token");
            return value;
        }

        internal static bool ReadBoolean(JToken token)
        {
            if (token.Type != JTokenType.Boolean) throw new FormatException("Expected boolean");
            return (bool)token;
        }

        private sealed class Scanner
        {
            private readonly string text;
            private int position;
            internal Scanner(string text) { this.text = text; }
            private FormatException Error() { return new FormatException("Invalid strict JSON at UTF-16 offset " + position); }
            internal void Validate() { Value(0); Space(); if (position != text.Length) throw Error(); }
            private void Space()
            {
                while (position < text.Length && (text[position] == ' ' || text[position] == '\t'
                    || text[position] == '\r' || text[position] == '\n')) position++;
            }
            private bool Take(char c)
            {
                if (position < text.Length && text[position] == c) { position++; return true; }
                return false;
            }
            private void Require(char c) { if (!Take(c)) throw Error(); }
            private void Literal(string value)
            {
                foreach (char c in value) Require(c);
            }
            private void Value(int depth)
            {
                if (depth > 64) throw new FormatException("JSON exceeds nesting limit");
                Space();
                if (position >= text.Length) throw Error();
                char c = text[position];
                if (c == '"') { String(); return; }
                if (Take('{'))
                {
                    Space(); if (Take('}')) return;
                    do { Space(); String(); Space(); Require(':'); Value(depth + 1); Space(); if (Take('}')) return; Require(','); } while (true);
                }
                if (Take('['))
                {
                    Space(); if (Take(']')) return;
                    do { Value(depth + 1); Space(); if (Take(']')) return; Require(','); } while (true);
                }
                if (c == 't') { Literal("true"); return; }
                if (c == 'f') { Literal("false"); return; }
                if (c == 'n') { Literal("null"); return; }
                Number();
            }
            private bool Digit() { return position < text.Length && text[position] >= '0' && text[position] <= '9'; }
            private void Digits() { if (!Digit()) throw Error(); while (Digit()) position++; }
            private void Number()
            {
                Take('-');
                if (!Take('0')) Digits();
                if (Take('.')) Digits();
                if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); Digits(); }
            }
            private int Hex4()
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (position >= text.Length) throw Error();
                    char c = text[position++];
                    int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                    if (digit < 0) throw Error();
                    value = value * 16 + digit;
                }
                return value;
            }
            private void String()
            {
                Require('"');
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"') return;
                    if (c < 0x20) throw Error();
                    if (c == '\\')
                    {
                        if (position >= text.Length) throw Error();
                        char escape = text[position++];
                        if (escape == 'u')
                        {
                            int scalar = Hex4();
                            if (scalar >= 0xD800 && scalar <= 0xDBFF)
                            {
                                Require('\\'); Require('u'); int low = Hex4();
                                if (low < 0xDC00 || low > 0xDFFF) throw Error();
                            }
                            else if (scalar >= 0xDC00 && scalar <= 0xDFFF) throw Error();
                        }
                        else if (escape != '"' && escape != '\\' && escape != '/' && escape != 'b'
                            && escape != 'f' && escape != 'n' && escape != 'r' && escape != 't') throw Error();
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (position >= text.Length || !char.IsLowSurrogate(text[position++])) throw Error();
                    }
                    else if (char.IsLowSurrogate(c)) throw Error();
                }
                throw Error();
            }
        }
`
