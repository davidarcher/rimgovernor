namespace RimGovernor.Brain.RemoteAccept;

/// <summary>A remote acceptance evidence failure: the Go <c>error</c> returned by <c>internal/remoteaccept</c> and <c>cmd/remoteaccept</c>. Every fail-closed check throws this.</summary>
public class RemoteAcceptException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RemoteAcceptException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public RemoteAcceptException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>A command-line mistake (unknown command or flag, malformed value, missing required flag); the command prints it and exits 2.</summary>
public sealed class RemoteAcceptUsageException : RemoteAcceptException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RemoteAcceptUsageException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// <see cref="RemoteEvidence.Evaluate"/> failed part way. Go returned the partially filled <c>Evaluation</c> beside the
/// error and <c>aggregate</c> still wrote it as an incomplete verdict, so the partial state travels with the exception.
/// </summary>
public sealed class EvaluationFailedException : RemoteAcceptException
{
    /// <summary>Creates the exception for <paramref name="message"/> carrying the state reached so far.</summary>
    public EvaluationFailedException(string message, Evaluation partial, Exception? inner)
        : base(message, inner)
    {
        Partial = partial;
    }

    /// <summary>The evaluation as it stood when the check failed.</summary>
    public Evaluation Partial { get; }
}
