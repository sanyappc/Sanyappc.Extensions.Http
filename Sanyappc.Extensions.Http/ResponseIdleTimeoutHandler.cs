using System.Net;

namespace Sanyappc.Extensions.Http;

/// <summary>
/// Fails the read of an HTTP response body that goes silent for longer than a configured time.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no built-in timeout for reading a response body. <see cref="HttpClient.Timeout"/> stops applying once the
/// headers arrive when a request is sent with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, and resilience
/// pipelines complete at the headers as well. A connection that goes half-open in the middle of a body can therefore
/// hold a read for as long as the caller's token allows.
/// </para>
/// <para>
/// The timer is re-armed before every read, so it bounds silence and never the length of a transfer: a slow response
/// that keeps producing data is not cut. When the body does go silent, the read throws a
/// <see cref="ResponseIdleTimeoutException"/> (a <see cref="TimeoutException"/>) naming the request method, scheme, host
/// and path. The query string and user information are never included, because they often carry credentials.
/// </para>
/// <para>Synchronous reads are passed through unbounded, because an idle timer needs a cancellable read.</para>
/// </remarks>
public sealed class ResponseIdleTimeoutHandler : DelegatingHandler
{
    // CancellationTokenSource.CancelAfter accepts nothing longer.
    private static readonly TimeSpan MaxIdleTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly TimeSpan idleTimeout;

    /// <summary>
    /// Creates the handler without an inner handler, for use with <c>IHttpClientFactory</c>.
    /// </summary>
    /// <param name="idleTimeout">How long the response body may go silent. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeout"/> is not positive or is too large for a timer.</exception>
    public ResponseIdleTimeoutHandler(TimeSpan idleTimeout)
    {
        ThrowIfInvalid(idleTimeout);
        this.idleTimeout = idleTimeout;
    }

    /// <summary>
    /// Creates the handler over <paramref name="innerHandler"/>, for use without <c>IHttpClientFactory</c>.
    /// </summary>
    /// <param name="idleTimeout">How long the response body may go silent. Must be positive.</param>
    /// <param name="innerHandler">The handler that sends the request.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeout"/> is not positive or is too large for a timer.</exception>
    public ResponseIdleTimeoutHandler(TimeSpan idleTimeout, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ThrowIfInvalid(idleTimeout);
        this.idleTimeout = idleTimeout;
    }

    private static string Describe(HttpRequestMessage request)
    {
        string endpoint = request.RequestUri is { IsAbsoluteUri: true } uri
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "an unknown endpoint";

        return $"{request.Method} {endpoint}";
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new IdleBoundContent(response.Content, idleTimeout, Describe(request));
        return response;
    }

    internal static void ThrowIfInvalid(TimeSpan idleTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(idleTimeout, MaxIdleTimeout);
    }

    private sealed class IdleBoundContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly TimeSpan idleTimeout;
        private readonly string request;

        public IdleBoundContent(HttpContent inner, TimeSpan idleTimeout, string request)
        {
            this.inner = inner;
            this.idleTimeout = idleTimeout;
            this.request = request;

            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Stream body = await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new IdleBoundStream(body, idleTimeout, request);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Stream body = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
                await body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }
    }

    private sealed class IdleBoundStream(Stream inner, TimeSpan idleTimeout, string request) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Re-armed per read, which is what makes this a bound on silence rather than on the whole transfer.
            using CancellationTokenSource silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            silence.CancelAfter(idleTimeout);
            try
            {
                return await inner.ReadAsync(buffer, silence.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ResponseIdleTimeoutException($"The response body of {request} went silent for {idleTimeout}.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
