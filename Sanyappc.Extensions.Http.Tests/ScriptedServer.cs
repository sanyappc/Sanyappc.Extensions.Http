using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sanyappc.Extensions.Http.Tests;

// An HTTP endpoint that misbehaves on cue: each connection's request headers are read, then the script answers.
internal sealed class ScriptedServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly List<TcpClient> connections = [];
    private readonly Func<NetworkStream, CancellationToken, Task> script;
    private readonly Task accepting;

    public ScriptedServer(Func<NetworkStream, CancellationToken, Task> script)
    {
        this.script = script;
        listener.Start();
        accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task ReadRequestHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        StringBuilder received = new();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient connection = await listener.AcceptTcpClientAsync(stopping.Token);
                lock (connections)
                    connections.Add(connection);

                _ = ServeAsync(connection.GetStream());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync(NetworkStream stream)
    {
        try
        {
            await ReadRequestHeadersAsync(stream, stopping.Token);
            await script(stream, stopping.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await accepting;
        listener.Dispose();

        lock (connections)
        {
            foreach (TcpClient connection in connections)
                connection.Dispose();
        }

        stopping.Dispose();
    }
}
