using System.Text.Json;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// Reads <c>go test -json</c> a line at a time, writes the text go test would have printed (package lines, plus a
/// failed test's own output) and collects the tests that ran (not skipped, so not marked slow) longer than
/// <see cref="SlowBudget"/>. Lines that are not JSON pass through (Go: <c>internal/affected/slowwarn.go</c>).
/// </summary>
internal sealed class SlowTestRenderer(TextWriter writer)
{
    /// <summary>
    /// The duration past which a test that ran under -short should carry the slowtest.Skip marker. A warning only:
    /// under load durations inflate, so the tool never fails on it; the nightly enforces it.
    /// </summary>
    public static readonly TimeSpan SlowBudget = TimeSpan.FromSeconds(1);

    private readonly Dictionary<(string Package, string Test), List<string>> _buffered = [];
    private readonly List<(string Name, TimeSpan Elapsed)> _slow = [];

    public void Line(string line)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            writer.WriteLine(line);
            return;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            writer.WriteLine(line);
            return;
        }
        var action = Text(root, "Action");
        var package = Text(root, "Package");
        var test = Text(root, "Test");
        var key = (package, test);
        switch (action)
        {
            case "output":
                var output = Text(root, "Output");
                if (test.Length == 0)
                {
                    if (output != "PASS\n") // -json emits it; plain go test does not
                    {
                        writer.Write(output);
                    }
                }
                else
                {
                    if (!_buffered.TryGetValue(key, out var lines))
                    {
                        _buffered[key] = lines = [];
                    }
                    lines.Add(output);
                }
                break;
            case "fail":
            case "pass":
                if (action == "fail" && _buffered.TryGetValue(key, out var failed))
                {
                    foreach (var text in failed)
                    {
                        writer.Write(text);
                    }
                }
                var elapsed = TimeSpan.FromSeconds(root.TryGetProperty("Elapsed", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0);
                if (test.Length > 0 && elapsed > SlowBudget)
                {
                    _slow.Add((package + "." + test, elapsed));
                }
                _buffered.Remove(key);
                break;
            case "skip":
                _buffered.Remove(key);
                break;
        }
    }

    /// <summary>Prints the unmarked slow tests, slowest first; nothing when there are none.</summary>
    public void Warn()
    {
        if (_slow.Count == 0)
        {
            return;
        }
        writer.WriteLine($"warning: {_slow.Count} test(s) took over {SlowBudget.TotalSeconds:0}s under -short without a slowtest.Skip marker (not a failure):");
        foreach (var (name, elapsed) in _slow.OrderByDescending(s => s.Elapsed))
        {
            writer.WriteLine($"  {name} took {(long)elapsed.TotalMilliseconds}ms");
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
