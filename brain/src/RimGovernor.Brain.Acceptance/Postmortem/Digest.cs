using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One finding of the digest (Go <c>postmortem.Line</c>): <see cref="Text"/> is the reading, <see cref="Evidence"/> the
/// file (relative to the case directory) and row it came from ("otlp/logs.jsonl#1834", "service.sqlite transitions#412",
/// "otlp/logs.jsonl.2#77"; a log row's number is its line in the segment).
/// </summary>
public sealed record DigestLine(string Text, string Evidence);

/// <summary>
/// One step of the diagnosis sequence (Go <c>postmortem.Section</c>); <see cref="Lines"/> are its findings, each with the
/// evidence it was read from. An empty <see cref="Lines"/> means the step found nothing and says so in
/// <see cref="Note"/>.
/// </summary>
public sealed class DigestSection
{
    public DigestSection(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>What the step could not read or found nothing of; empty for none.</summary>
    public string Note { get; set; } = "";

    /// <summary>The findings in reading order.</summary>
    public List<DigestLine> Lines { get; } = [];

    internal JsonObject ToJson()
    {
        var section = new JsonObject { ["name"] = Name };
        if (Note.Length > 0)
        {
            section["note"] = Note;
        }

        if (Lines.Count > 0)
        {
            var lines = new JsonArray();
            foreach (var line in Lines)
            {
                lines.Add(new JsonObject { ["text"] = line.Text, ["evidence"] = line.Evidence });
            }

            section["lines"] = lines;
        }

        return section;
    }
}

/// <summary>
/// The ordered postmortem of one case output directory (Go <c>postmortem.Digest</c>): the manual sequence every
/// diagnosis started with, read from the case's own evidence in a fixed order. Each line names the file and row it came
/// from so the digest is checkable, not a verdict. <see cref="ToJson"/> is what the report records as <c>diagnosis</c>.
/// </summary>
public sealed class Digest
{
    public Digest(string caseName, string error, IReadOnlyList<DigestSection> sections)
    {
        Case = caseName;
        Error = error;
        Sections = sections;
    }

    /// <summary>The case name, from result.json (or the live report).</summary>
    public string Case { get; }

    /// <summary>The run's error, from result.json (or the live report).</summary>
    public string Error { get; }

    /// <summary>In the order the digest is read: revision, stage graph, refusals, rounds, colony extent, unsuccessful stages, job failures, authority generations.</summary>
    public IReadOnlyList<DigestSection> Sections { get; }

    /// <summary>The digest as the report's <c>diagnosis</c> value: <c>case</c> and <c>error</c> when set, then <c>sections</c>.</summary>
    public JsonObject ToJson()
    {
        var digest = new JsonObject();
        if (Case.Length > 0)
        {
            digest["case"] = Case;
        }

        if (Error.Length > 0)
        {
            digest["error"] = Error;
        }

        var sections = new JsonArray();
        foreach (var section in Sections)
        {
            sections.Add(section.ToJson());
        }

        digest["sections"] = sections;
        return digest;
    }

    /// <summary>Renders the digest as the lines <c>acceptance why</c> prints.</summary>
    public string Text()
    {
        var b = new StringBuilder();
        if (Case.Length > 0)
        {
            b.Append("case: ").Append(Case).Append('\n');
        }

        if (Error.Length > 0)
        {
            b.Append("error: ").Append(Error).Append('\n');
        }

        foreach (var s in Sections)
        {
            b.Append("## ").Append(s.Name).Append('\n');
            if (s.Note.Length > 0)
            {
                b.Append("  ").Append(s.Note).Append('\n');
            }

            foreach (var l in s.Lines)
            {
                b.Append("  - ").Append(l.Text).Append("  [").Append(l.Evidence).Append("]\n");
            }
        }

        return b.ToString();
    }

    /// <summary>Renders the digest to <paramref name="writer"/>.</summary>
    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Text());
    }
}
