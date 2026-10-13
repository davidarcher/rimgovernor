namespace RimGovernor.Tools.Common;

/// <summary>A failure to report as "tool: message" with exit code 1 (Go: a returned error).</summary>
internal sealed class ToolException : Exception
{
    public ToolException(string message)
        : base(message) { }

    public ToolException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>A bad command line: print the usage and exit 2.</summary>
internal sealed class UsageException(string message) : Exception(message);
