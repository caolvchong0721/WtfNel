using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Install;

/// <summary>
/// Ported from FandNEL's FastHttpDownloader: when the server supports Range, downloads in 8 concurrent
/// segments (each retried 3 times), otherwise sequentially. Writes a temporary .part and only replaces
/// the target file after the md5 check passes.
/// </summary>
public static class FastDownloader
{
    private const int BufferSize = 128 * 1024;
    private const int MaxConcurrentSegments = 8;
    private const int SegmentRetryCount = 3;
    private const long MinimumSegmentedSize = 1 << 20;
    private const long TargetSegmentSize = 4 << 20;

    /// <summary>Downloads to <paramref name="destination"/>; skips if it already exists with a matching md5.</summary>
    public static async Task DownloadAsync(HttpClient http, string url, string destination, string? md5, CancellationToken ct = default)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https")) throw new InvalidDataException("下载地址必须是 HTTP/HTTPS: " + url);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (File.Exists(destination) && !string.IsNullOrEmpty(md5) && await Md5MatchesAsync(destination, md5, ct)) return;

        var name = Path.GetFileName(destination);
        var temp = destination + $".{Guid.NewGuid():N}.part";
        using var bar = new ProgressLine(name);
        void Report(long done, long? total) => bar.Update(done, total);

        try
        {
            Log.Debug($"[dl] 下载 {name} <- {url}");
            var meta = await TryGetMetadataAsync(http, uri, ct);
            var done = false;
            if (meta is { SupportsRanges: true } m && m.Length >= MinimumSegmentedSize)
            {
                try
                {
                    await DownloadInSegmentsAsync(http, uri, temp, m.Length, Report, ct);
                    done = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    Log.Warn($"[dl] 分段下载失败，改为顺序下载: {ex.GetBaseException().Message}");
                    TryDelete(temp);
                }
            }
            if (!done) await DownloadSingleAsync(http, uri, temp, Report, ct);

            if (!string.IsNullOrEmpty(md5) && !await Md5MatchesAsync(temp, md5, ct))
                throw new InvalidDataException($"MD5 校验失败: {name}（期望 {md5}）");
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
            File.Move(temp, destination, overwrite: true);
            bar.Done();
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static async Task<bool> Md5MatchesAsync(string path, string md5, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await MD5.HashDataAsync(input, ct)).Equals(md5, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(long Length, bool SupportsRanges)?> TryGetMetadataAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, uri);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength is not { } len) return null;
            return (len, resp.Headers.AcceptRanges.Contains("bytes"));
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task DownloadInSegmentsAsync(HttpClient http, Uri uri, string destination, long total,
        Action<long, long?> report, CancellationToken ct)
    {
        var count = Math.Min(MaxConcurrentSegments * 3, Math.Max(2, (int)Math.Ceiling(total / (double)TargetSegmentSize)));
        using (var mapped = MemoryMappedFile.CreateFromFile(destination, FileMode.Create, null, total, MemoryMappedFileAccess.ReadWrite))
        {
            using var gate = new SemaphoreSlim(MaxConcurrentSegments);
            var errors = new ConcurrentBag<Exception>();
            long transferred = 0;

            var seg = total / count;
            var tasks = Enumerable.Range(0, count).Select(i =>
            {
                var start = i * seg;
                var end = i == count - 1 ? total - 1 : (i + 1) * seg - 1;
                return RangeAsync(start, end);
            });
            await Task.WhenAll(tasks);
            if (!errors.IsEmpty) throw new AggregateException(errors);
            report(transferred, total);

            async Task RangeAsync(long start, long end)
            {
                await gate.WaitAsync(ct);
                try
                {
                    for (var attempt = 1; attempt <= SegmentRetryCount; attempt++)
                    {
                        long got = 0;
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                            req.Headers.Range = new RangeHeaderValue(start, end);
                            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                            resp.EnsureSuccessStatusCode();
                            if (resp.StatusCode != HttpStatusCode.PartialContent)
                                throw new InvalidDataException("服务器未返回分段响应");
                            var cr = resp.Content.Headers.ContentRange;
                            if (cr is not null && (cr.From != start || cr.To != end))
                                throw new InvalidDataException("服务器返回了错误的分段范围");

                            await using var input = await resp.Content.ReadAsStreamAsync(ct);
                            await using var output = mapped.CreateViewStream(start, end - start + 1, MemoryMappedFileAccess.Write);
                            var buf = new byte[BufferSize];
                            int n;
                            while ((n = await input.ReadAsync(buf, ct)) > 0)
                            {
                                await output.WriteAsync(buf.AsMemory(0, n), ct);
                                got += n;
                                report(Interlocked.Add(ref transferred, n), total);
                            }
                            if (got != end - start + 1) throw new InvalidDataException("分段下载不完整");
                            return;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Add(ref transferred, -got);
                            if (attempt == SegmentRetryCount) { errors.Add(ex); return; }
                            await Task.Delay(250 * attempt, ct);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
    }

    private static async Task DownloadSingleAsync(HttpClient http, Uri uri, string destination, Action<long, long?> report, CancellationToken ct)
    {
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        var buf = new byte[BufferSize];
        long done = 0;
        int n;
        while ((n = await input.ReadAsync(buf, ct)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, n), ct);
            report(done += n, total);
        }
        if (total is { } t && done != t) throw new InvalidDataException("下载不完整");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}

/// <summary>
/// Single-line download progress: <c>\ [██████··········] 45%  name  12/30 MB</c>.
/// The leading character spins one step every 100ms (| / - \), independent of byte progress, so the
/// program is visibly alive even when a transfer stalls.
/// </summary>
internal sealed class ProgressLine : IDisposable
{
    private const int BarWidth = 30;
    private static readonly string[] Spin = { "|", "/", "-", @"\" };

    private readonly string _name;
    private readonly Timer _timer;
    private long _done;
    private long _total;
    private int _frame;
    private readonly object _gate = new();
    private bool _stopped;

    public ProgressLine(string name)
    {
        _name = name.Length > 32 ? name[..31] + "…" : name;
        _timer = new Timer(_ => Render(), null, 0, 100);
    }

    public void Update(long done, long? total)
    {
        Interlocked.Exchange(ref _done, done);
        if (total is > 0) Interlocked.Exchange(ref _total, total.Value);
    }

    private void Render()
    {
        lock (_gate)
        {
            if (_stopped) return;
            var spin = Spin[++_frame % Spin.Length];
            Log.Status(Line(spin, Interlocked.Read(ref _done), Interlocked.Read(ref _total)));
        }
    }

    private static char Full => WtfNel.Core.Terminal.Term.AmbiguousWide ? '#' : '█';
    private static char Empty => WtfNel.Core.Terminal.Term.AmbiguousWide ? '-' : '·';

    private string Line(string spin, long done, long total)
    {
        var pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
        var fill = pct * BarWidth / 100;
        var size = total > 0 ? $"{done >> 20}/{total >> 20} MB" : $"{done >> 20} MB";
        return $"[mediumpurple]{spin}[/] [[[green]{new string(Full, fill)}[/][grey]{new string(Empty, BarWidth - fill)}[/]]] {pct,3}%  {Spectre.Console.Markup.Escape(_name)}  [grey]{size}[/]";
    }

    /// <summary>Download finished: stop spinning and leave a single 100% result line.</summary>
    public void Done()
    {
        if (!Stop()) return;
        var total = Interlocked.Read(ref _total);
        Log.Info($"✓ {_name}  {(total > 0 ? total : Interlocked.Read(ref _done)) >> 20} MB");
    }

    public void Dispose() => Stop();

    private bool Stop()
    {
        lock (_gate)
        {
            if (_stopped) return false;
            _stopped = true;
            _timer.Dispose();
            Log.Status(null);
            return true;
        }
    }
}
