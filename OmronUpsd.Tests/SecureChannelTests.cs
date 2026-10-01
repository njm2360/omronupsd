using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OmronUpsd.Tests;

public class SecureChannelTests
{
    private const string Key = "0123456789abcdef-test-key";
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task<(TcpClient Server, TcpClient Client)> ConnectPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(Ct);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        var server = await accept;
        listener.Stop();
        return (server, client);
    }

    [Fact]
    public async Task MutualAuth_RoundTrip()
    {
        var (s, c) = await ConnectPairAsync();
        var accept = SecureChannel.AcceptAsync(s.GetStream(), Key, Ct);
        await using var client = await SecureChannel.ConnectAsync(c.GetStream(), Key, "pc2", Ct);
        var (server, name) = await accept;
        await using var _ = server;

        Assert.Equal("pc2", name);

        await server.SendAsync(MessageTypes.Shutdown, new ShutdownCommand(true, "Timer"), Ct);
        var (type, data) = await client.ReceiveAsync(Ct);
        Assert.Equal(MessageTypes.Shutdown, type);
        Assert.Equal(new ShutdownCommand(true, "Timer"), SecureChannel.Decode<ShutdownCommand>(data));

        await client.SendAsync(MessageTypes.ShutdownAck, Ct);
        Assert.Equal(MessageTypes.ShutdownAck, (await server.ReceiveAsync(Ct)).Type);
    }

    [Fact]
    public async Task WrongKey_Rejected()
    {
        var (s, c) = await ConnectPairAsync();
        var accept = SecureChannel.AcceptAsync(s.GetStream(), Key, Ct);
        var connect = SecureChannel.ConnectAsync(c.GetStream(), Key + "x", "pc2", Ct);

        await Assert.ThrowsAsync<ProtocolException>(() => accept);
        s.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => connect);
    }

    [Fact]
    public async Task FakeMaster_RejectedByClient()
    {
        var (s, c) = await ConnectPairAsync();
        var fakeMaster = SecureChannel.AcceptAsync(s.GetStream(), Key + "x", Ct);
        var connect = SecureChannel.ConnectAsync(c.GetStream(), Key, "pc2", Ct);

        await Assert.ThrowsAsync<ProtocolException>(() => fakeMaster);
        s.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => connect);
    }

    [Fact]
    public async Task Replay_Rejected()
    {
        var (s, c) = await ConnectPairAsync();
        var tap = new TapStream(s.GetStream());
        var accept = SecureChannel.AcceptAsync(tap, Key, Ct);
        await using var client = await SecureChannel.ConnectAsync(c.GetStream(), Key, "pc2", Ct);
        var (server, _) = await accept;
        await using var __ = server;

        tap.Written.SetLength(0);
        await server.SendAsync(MessageTypes.Shutdown, new ShutdownCommand(false, "Timer"), Ct);
        await client.ReceiveAsync(Ct);

        await s.GetStream().WriteAsync(tap.Written.ToArray(), Ct);
        await Assert.ThrowsAsync<ProtocolException>(() => client.ReceiveAsync(Ct));
    }

    private sealed class TapStream(Stream inner) : Stream
    {
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Written.Write(buffer.Span);
            await inner.WriteAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task OversizedLine_Rejected()
    {
        var (s, c) = await ConnectPairAsync();
        var accept = SecureChannel.AcceptAsync(s.GetStream(), Key, Ct);
        await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes(new string('a', 20_000) + "\n"), Ct);
        await Assert.ThrowsAsync<ProtocolException>(() => accept);
    }
}
