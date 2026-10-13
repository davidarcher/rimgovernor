namespace RimGovernor.Launcher;

/// <summary>A failure whose message is written for the player: the page shows it as it is.</summary>
public sealed class LauncherException : Exception
{
    public LauncherException(string message)
        : base(message)
    {
    }

    public LauncherException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
