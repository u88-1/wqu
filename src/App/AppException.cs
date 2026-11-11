namespace GeminiMenu;

public enum ErrKind { NoKey, BadKey, Quota, Overloaded, Network, NotFound, Blocked, BadRequest, Unsupported, FileProblem, Other }

/// <summary>An error we know how to explain to the user in Hebrew.</summary>
public sealed class AppException : Exception
{
    public ErrKind Kind { get; }
    public int Status { get; }
    public string? Technical { get; }
    /// <summary>Set for errors from Claude or OpenAI, so the error page does not blame Gemini.</summary>
    public string? Provider { get; init; }

    public AppException(ErrKind kind, string message, int status = 0, string? technical = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Status = status;
        Technical = technical;
    }
}
