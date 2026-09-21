using Microsoft.Extensions.DependencyInjection;

namespace Sanyappc.Extensions.Http;

/// <summary>
/// Registers <see cref="ResponseIdleTimeoutHandler"/> on clients built by <c>IHttpClientFactory</c>.
/// </summary>
public static class ResponseIdleTimeoutHttpClientBuilderExtensions
{
    /// <summary>
    /// Fails any response body read on this client that goes silent for longer than <paramref name="idleTimeout"/>.
    /// </summary>
    /// <remarks>
    /// The order relative to other handlers does not affect correctness. Adding it before a resilience handler makes it
    /// the outer one, so only the response the pipeline finally returns is wrapped, not the ones a retry discards.
    /// </remarks>
    /// <param name="builder">The client builder.</param>
    /// <param name="idleTimeout">How long the response body may go silent. Must be positive.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeout"/> is not positive or is too large for a timer.</exception>
    public static IHttpClientBuilder AddResponseIdleTimeout(this IHttpClientBuilder builder, TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ResponseIdleTimeoutHandler.ThrowIfInvalid(idleTimeout);

        return builder.AddHttpMessageHandler(() => new ResponseIdleTimeoutHandler(idleTimeout));
    }
}
