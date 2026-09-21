using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

namespace Sanyappc.Extensions.Http.Tests;

// Against a local socket: the hang lives between HttpClient and its handler chain, where a mocked handler cannot put it.
public sealed class ResponseIdleTimeoutTests : IAsyncDisposable
{
    private const string ClientName = "bounded";

    // A gap four times shorter than the timeout, so a loaded agent stretching one delay cannot turn a live body into a silent one.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TrickleGap = TimeSpan.FromMilliseconds(250);

    private static readonly string[] Items = [.. Enumerable.Range(1, 8).Select(number => $"{{\"a\":{number}}}")];
    private static readonly string WholeBody = "[" + string.Join(",", Items) + "]";

    private readonly List<ScriptedServer> servers = [];
    private readonly List<ServiceProvider> providers = [];

    private HttpClient ClientWith(Action<IHttpClientBuilder> register)
    {
        ServiceCollection registrations = new();
        register(registrations.AddHttpClient(ClientName));

        ServiceProvider provider = registrations.BuildServiceProvider();
        providers.Add(provider);
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
    }

    private HttpClient BoundedClient() =>
        ClientWith(builder => builder.AddResponseIdleTimeout(IdleTimeout));

    private Uri UrlServing(Func<NetworkStream, CancellationToken, Task> script)
    {
        ScriptedServer server = new(script);
        servers.Add(server);
        return new Uri($"http://127.0.0.1:{server.Port}/items?token=secret");
    }

    private static async Task SendAsync(NetworkStream stream, string text, CancellationToken stopping)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), stopping);
        await stream.FlushAsync(stopping);
    }

    private static string Headers(int contentLength) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {contentLength}\r\n\r\n";

    private static async Task ReadToEndAsync(Stream body, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        while (await body.ReadAsync(buffer, cancellationToken) > 0)
        {
        }
    }

    // Headers and half a body, then silence: what a connection that went half-open mid-response looks like.
    private static async Task StartABodyThenGoSilent(NetworkStream stream, CancellationToken stopping)
    {
        await SendAsync(stream, Headers(1000) + "[{\"a\":1},", stopping);
        await Task.Delay(Timeout.Infinite, stopping);
    }

    // Every gap is under the idle timeout and the whole transfer takes twice as long as it.
    private static async Task TrickleTheBody(NetworkStream stream, CancellationToken stopping)
    {
        await SendAsync(stream, Headers(WholeBody.Length), stopping);

        int chunk = (WholeBody.Length + Items.Length - 1) / Items.Length;
        for (int offset = 0; offset < WholeBody.Length; offset += chunk)
        {
            await Task.Delay(TrickleGap, stopping);
            await SendAsync(stream, WholeBody.Substring(offset, Math.Min(chunk, WholeBody.Length - offset)), stopping);
        }
    }

    private static Task AnswerAtOnce(NetworkStream stream, CancellationToken stopping) =>
        SendAsync(stream, Headers(WholeBody.Length) + WholeBody, stopping);

    [Fact]
    public async Task AJsonBodyThatGoesSilentEndsAtTheIdleTimeout()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(StartABodyThenGoSilent);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetFromJsonAsync<List<Dictionary<string, int>>>(url, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABufferedBodyThatGoesSilentEndsAtTheIdleTimeout()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(StartABodyThenGoSilent);

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync(url, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStreamedBodyThatGoesSilentEndsAtTheIdleTimeout()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(StartABodyThenGoSilent);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(() => ReadToEndAsync(body, cancellationToken));
    }

    [Fact]
    public async Task TheTimeoutNamesTheEndpointAndNeverItsQuery()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(StartABodyThenGoSilent);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() => ReadToEndAsync(body, cancellationToken));

        Assert.Contains($"GET http://127.0.0.1:{url.Port}/items went silent", failure.Message);
        Assert.DoesNotContain("secret", failure.Message);
    }

    [Fact]
    public async Task ASlowBodyThatKeepsArrivingIsNeverCut()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(TrickleTheBody);

        List<Dictionary<string, int>>? parsed = await client.GetFromJsonAsync<List<Dictionary<string, int>>>(url, TestContext.Current.CancellationToken);

        Assert.Equal(Items.Length, parsed?.Count);
    }

    [Fact]
    public async Task ABufferedBodyArrivesIntactWithItsHeaders()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(AnswerAtOnce);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(WholeBody.Length, response.Content.Headers.ContentLength);
        Assert.Equal(WholeBody, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task TheCallersOwnCancellationStaysACancellation()
    {
        HttpClient client = BoundedClient();
        Uri url = UrlServing(StartABodyThenGoSilent);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(IdleTimeout / 4);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetFromJsonAsync<List<Dictionary<string, int>>>(url, caller.Token));
    }

    [Fact]
    public async Task ItBoundsTheBodyBehindTheStandardResilienceHandlerWhichDoesNot()
    {
        HttpClient client = ClientWith(builder => builder.AddResponseIdleTimeout(IdleTimeout).AddStandardResilienceHandler());
        Uri url = UrlServing(StartABodyThenGoSilent);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetFromJsonAsync<List<Dictionary<string, int>>>(url, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ItWorksWithoutTheFactory()
    {
        using SocketsHttpHandler transport = new();
        using ResponseIdleTimeoutHandler handler = new(IdleTimeout, transport);
        using HttpClient client = new(handler);
        Uri url = UrlServing(StartABodyThenGoSilent);

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync(url, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ATimeoutThatIsNotPositiveIsRejectedAtRegistration(int seconds)
    {
        ServiceCollection registrations = new();
        IHttpClientBuilder builder = registrations.AddHttpClient(ClientName);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddResponseIdleTimeout(TimeSpan.FromSeconds(seconds)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in providers)
            await provider.DisposeAsync();

        foreach (ScriptedServer server in servers)
            await server.DisposeAsync();
    }
}
