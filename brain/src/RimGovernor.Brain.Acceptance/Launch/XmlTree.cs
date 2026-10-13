using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// RimWorld's config XML (ModsConfig.xml, About.xml) as a mutable tree that keeps elements and the indentation
/// whitespace between them in document order (Go <c>xmltree.go</c>'s hand-rolled <c>xmlElem</c>; LINQ to XML with
/// preserved whitespace does the same job). The files are written with <c>encoding='utf8'</c>, which the XML
/// reader would reject as an encoding name when it opens the bytes itself, so the text is decoded here and parsed from
/// a string, where the declaration's encoding is not consulted.
/// </summary>
internal static class XmlTree
{
    /// <summary>Parses XML text into its declaration (as written, empty when there is none) and root element.</summary>
    internal static (string Header, XElement Root) Parse(string text)
    {
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(text.TrimStart('﻿')),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XElement root = document.Root ?? throw new AcceptanceException("parse xml: no root element");
            return (document.Declaration?.ToString() ?? "", root);
        }
        catch (XmlException e)
        {
            throw new AcceptanceException("parse xml: " + e.Message, e);
        }
    }

    /// <summary>Reads and parses the file at <paramref name="path"/>.</summary>
    internal static (string Header, XElement Root) ParseFile(string path)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"read {path}: {e.Message}", e);
        }

        return Parse(text);
    }

    /// <summary>The document text for a header and root, with no formatting added.</summary>
    internal static string Write(string header, XElement root) => header + root.ToString(SaveOptions.DisableFormatting);

    /// <summary>The literal text directly under <paramref name="element"/> (not its descendants').</summary>
    internal static string DirectText(this XElement element) => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));

    /// <summary>The direct text of the named child, empty when there is none.</summary>
    internal static string ChildText(this XElement element, string name) => element.Element(name)?.DirectText() ?? "";
}
