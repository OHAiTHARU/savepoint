using System.Net;

namespace Savepoint.Core.Api;

/// <summary>The server answered with an error body ({"error": {...}}).</summary>
public class ApiException : Exception
{
    public ApiException(HttpStatusCode status, string code, string message,
        IReadOnlyList<string>? missing = null, string? headVersionId = null)
        : base($"{(int)status} {code}: {message}")
    {
        Status = status;
        Code = code;
        Missing = missing ?? Array.Empty<string>();
        HeadVersionId = headVersionId;
    }

    public HttpStatusCode Status { get; }
    public string Code { get; }
    public IReadOnlyList<string> Missing { get; }
    public string? HeadVersionId { get; }

    public bool IsHeadMoved => Status == HttpStatusCode.Conflict && Code == "head_moved";
    public bool IsMissingBlobs => Code == "missing_blobs";
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>The server could not be reached (network error, timeout, 5xx, proxy error).
/// Work that fails this way is queued and retried.</summary>
public class ServerUnreachableException : Exception
{
    public ServerUnreachableException(string message, Exception? inner = null) : base(message, inner) { }
}
