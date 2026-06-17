using System.Net;
using System.Net.Http;
using Foundry.Core.Update;

namespace Foundry.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("v0.5.0", "0.4.1", true)]   // newer
    [InlineData("0.4.2", "0.4.1", true)]    // newer, no 'v'
    [InlineData("v0.4.1", "0.4.1", false)]  // equal
    [InlineData("v0.4.0", "0.4.1", false)]  // older
    [InlineData("v1.0.0-beta", "0.4.1", true)] // pre-release metadata stripped, still newer
    public void IsNewer_ComparesSemverTolerantOfPrefixAndMetadata(string tag, string current, bool expected)
    {
        Assert.Equal(expected, GitHubUpdater.IsNewer(tag, current));
    }

    // ---- DownloadAsync stall protection ------------------------------------------------------------
    // The body stream is read with HttpCompletionOption.ResponseHeadersRead, so the HttpClient.Timeout does
    // NOT bound it — a mid-download stall would hang the update forever. DownloadAsync must abort if no bytes
    // arrive within a stall window.

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public BytesHandler(byte[] body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    // Serves one byte then stalls forever (respecting cancellation), simulating a wedged connection mid-body.
    private sealed class StallingStream : Stream
    {
        private bool _served;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_served) { _served = true; buffer.Span[0] = 0x42; return 1; }
            await Task.Delay(Timeout.Infinite, ct);   // stall until the watchdog cancels
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
    }

    private static string TempName() => "foundry_upd_" + Guid.NewGuid().ToString("N")[..8] + ".bin";

    [Fact(Timeout = 10000)]
    public async Task DownloadAsync_StalledConnection_AbortsWithinStallWindow()
    {
        using var http = new HttpClient(new StallingHandler());
        var updater = new GitHubUpdater(http);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            updater.DownloadAsync("https://x/installer.exe", TempName(), stallTimeout: TimeSpan.FromMilliseconds(300)));
    }

    [Fact(Timeout = 10000)]
    public async Task DownloadAsync_NormalResponse_WritesFile()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("installer-bytes");
        using var http = new HttpClient(new BytesHandler(body));
        var updater = new GitHubUpdater(http);
        var path = await updater.DownloadAsync("https://x/installer.exe", TempName(), stallTimeout: TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(body, await File.ReadAllBytesAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
