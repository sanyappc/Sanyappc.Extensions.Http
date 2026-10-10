# Sanyappc.Extensions.Http

[![NuGet](https://img.shields.io/nuget/v/Sanyappc.Extensions.Http)](https://www.nuget.org/packages/Sanyappc.Extensions.Http)

`HttpClient` extensions for .NET. Adds the response-body idle timeout that .NET does not have: a delegating handler that fails a body read once it goes silent for a configured time, without ever cutting a slow transfer that keeps producing data. Works with buffered sends, `GetFromJsonAsync` and streamed reads, composes with `IHttpClientFactory` and resilience handlers, and never leaks query strings into error messages.

## Installation

```
dotnet add package Sanyappc.Extensions.Http
```

## The problem

Nothing in .NET bounds the read of a response body:

- `HttpClient.Timeout` is a bound on the whole call, not on silence, and it ends at the headers when a request is sent with `HttpCompletionOption.ResponseHeadersRead`. A body read through `ReadAsStreamAsync` or `ReadFromJsonAsync` after such a send, which is how streaming API clients work, has no bound at all.
- `AddStandardResilienceHandler` from `Microsoft.Extensions.Http.Resilience` sets `HttpClient.Timeout` to infinite, and its attempt and total timeouts complete when the response headers arrive. Behind it, even a buffered send or `GetFromJsonAsync` reads the body unbounded.
- `SocketsHttpHandler` bounds connecting, `100-continue`, response draining, pooled connection idleness and HTTP/2 keep-alive pings. It has no read timeout for a body.

A connection that goes half-open in the middle of a body (a restarted proxy, a dropped NAT entry, a peer that stops sending) therefore holds the read for as long as the caller's token allows. A background job called without a deadline waits forever.

## Usage

```csharp
builder.Services.AddHttpClient<ItemsClient>()
    .AddResponseIdleTimeout(TimeSpan.FromSeconds(30));
```

With a resilience handler:

```csharp
builder.Services.AddHttpClient<ItemsClient>()
    .AddResponseIdleTimeout(TimeSpan.FromSeconds(30))
    .AddStandardResilienceHandler();
```

The order does not affect correctness. Adding the idle timeout first makes it the outer handler, so only the response the pipeline finally returns is wrapped, not the ones a retry discards.

Without `IHttpClientFactory`:

```csharp
SocketsHttpHandler transport = new();
ResponseIdleTimeoutHandler handler = new(TimeSpan.FromSeconds(30), transport);
HttpClient client = new(handler);
```

## Semantics

The timer is re-armed before every read of the response body, so it bounds **silence**, not the length of a transfer:

| Response body | Result |
|---|---|
| Arrives at any speed with gaps shorter than the timeout | Read to the end, however long it takes |
| Goes silent for longer than the timeout | The read throws `ResponseIdleTimeoutException`, a `TimeoutException` |
| Read is cancelled by the caller's token | `OperationCanceledException`, as without the handler |

It applies the same way to buffered sends (`GetAsync`, `PostAsync`, `SendAsync` with the default completion option), to `GetFromJsonAsync` and `ReadFromJsonAsync`, and to streams returned by `ReadAsStreamAsync`. Response headers, including `Content-Length` and `Content-Type`, are preserved.

Bounding the time to the response headers stays the job of `HttpClient.Timeout` or a resilience pipeline. The two complement each other: one bounds the wait for a response, the other bounds silence inside it.

## Error handling

A silent body surfaces as a `ResponseIdleTimeoutException`, which is a `TimeoutException`:

```
The response body of GET https://api.example.com/v1/items went silent for 00:00:30.
```

The message names the request method, scheme, host and path. The query string and user information are never included, because they often carry credentials.

```csharp
try
{
    Item[]? items = await client.GetFromJsonAsync<Item[]>("v1/items", cancellationToken);
}
catch (ResponseIdleTimeoutException)
{
    // the peer stopped sending in the middle of the body; a plain TimeoutException catch sees it too
}
```

## Choosing a timeout

Pick a value above the longest pause the server legitimately makes between two pieces of a body. For request/response APIs that is usually seconds. For streaming responses (server-sent events, token streams) it is the longest expected gap between events, so make sure the server sends keep-alive comments more often than the timeout.

The timeout must be positive and no longer than `CancellationTokenSource.CancelAfter` accepts (about 49.7 days). An invalid value throws `ArgumentOutOfRangeException` at registration.

## Limitations

- Synchronous reads (`Stream.Read`, `HttpClient.Send`) pass through unbounded, because an idle timer needs a cancellable read.
- A peer that keeps sending a byte at a time just inside the timeout is never cut. Bound the whole operation with the caller's `CancellationToken` when that matters.
- The timer covers the response body only. It does not bound the request body upload or the wait for response headers.
