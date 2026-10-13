using System.Text;

namespace RimGovernor.Launcher;

/// <summary>A <see cref="TextWriter"/> that hands every chunk written to it to a callback.</summary>
internal sealed class LineSink(Action<string> onText) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => onText(value.ToString());

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            onText(value);
        }
    }

    public override void Write(char[] buffer, int index, int count) => onText(new string(buffer, index, count));
}
