namespace Sanyappc.Extensions.Http;

/// <summary>
/// Thrown by <see cref="ResponseIdleTimeoutHandler"/> when a response body goes silent for longer than the idle timeout.
/// </summary>
/// <remarks>
/// A <see cref="TimeoutException"/>, so code that already handles those keeps working; a type of its own, so a caller can
/// tell a peer that stopped sending from every other timeout and answer it as the upstream failure it is.
/// </remarks>
public sealed class ResponseIdleTimeoutException : TimeoutException
{
    /// <summary>
    /// Creates the exception with the message the handler composes: the request's method, scheme, host and path and
    /// the timeout that passed, never the query string or user information.
    /// </summary>
    /// <param name="message">The message.</param>
    public ResponseIdleTimeoutException(string message)
        : base(message)
    {
    }
}
