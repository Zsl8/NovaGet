using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace NovaGet.TestServer;

/// <summary>
/// Kestrel on 127.0.0.1 with a random port. Serves registered <see cref="TestFile"/>s with configurable
/// range support, throttling, drops, validators and failures, plus:
/// <list type="bullet">
/// <item><c>/redirect/{n}/{path}</c> — n chained 302 redirects ending at <c>/{path}</c></item>
/// </list>
/// Every request is recorded in <see cref="Requests"/>.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, TestFile> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RequestRecord> _requests = new();

    private TestHttpServer(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseUri { get; private set; } = null!;

    public IReadOnlyCollection<RequestRecord> Requests => _requests;

    /// <param name="https">Serve https with a self-signed localhost certificate (clients must accept it).</param>
    public static async Task<TestHttpServer> StartAsync(int port = 0, bool https = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port, listen =>
            {
                if (https)
                {
                    listen.UseHttps(TestCertificates.CreateLocalhost());
                }
            });
            o.Limits.MaxConcurrentConnections = null;
            o.Limits.MinResponseDataRate = null;
        });
        var app = builder.Build();
        var server = new TestHttpServer(app);
        app.Run(server.HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUri = new Uri(address.EndsWith('/') ? address : address + "/");
        return server;
    }

    public TestFile AddFile(string path, long size, int seed = 1)
    {
        var file = new TestFile(path.TrimStart('/'), size, seed);
        _files[file.Path] = file;
        return file;
    }

    /// <summary>Serves fixed bytes (a playlist, a key, a media segment).</summary>
    public TestFile AddContent(string path, byte[] data, string contentType = "application/octet-stream")
    {
        var file = AddFile(path, data.Length);
        file.SetData(data);
        file.ContentType = contentType;
        return file;
    }

    public TestFile AddText(string path, string text, string contentType) =>
        AddContent(path, System.Text.Encoding.UTF8.GetBytes(text), contentType);

    public Uri UrlFor(string path) => new(BaseUri, path.TrimStart('/'));

    public Uri UrlFor(TestFile file) => UrlFor(file.Path);

    public IReadOnlyList<RequestRecord> RequestsFor(TestFile file) =>
        [.. _requests.Where(r => r.Path == "/" + file.Path)];

    public void ClearRequests() => _requests.Clear();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var recorded = 0;

        // Record before any byte reaches the client, so a test that just saw its download finish sees every request.
        context.Response.OnStarting(() =>
        {
            if (Interlocked.Exchange(ref recorded, 1) == 0)
            {
                Record(context, path);
            }

            return Task.CompletedTask;
        });
        try
        {
            if (path.StartsWith("/redirect/", StringComparison.Ordinal))
            {
                HandleRedirect(context, path);
                return;
            }

            if (!_files.TryGetValue(path.TrimStart('/'), out var file))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (file.RequireAuth is { } auth && !TestAuth.IsAuthorized(context.Request, auth))
            {
                TestAuth.Challenge(context.Response, auth.Scheme);
                return;
            }

            await ServeFileAsync(context, file).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref recorded, 1) == 0)
            {
                Record(context, path);
            }
        }
    }

    private void Record(HttpContext context, string path)
    {
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        _requests.Enqueue(new RequestRecord(
            context.Request.Method,
            path,
            headers.GetValueOrDefault(HeaderNames.Range),
            headers.GetValueOrDefault(HeaderNames.IfRange),
            context.Response.StatusCode,
            context.Connection.Id,
            headers,
            DateTimeOffset.UtcNow));
    }

    private static void HandleRedirect(HttpContext context, string path)
    {
        // /redirect/{n}/{rest}
        var rest = path["/redirect/".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var n = int.Parse(rest[..slash], CultureInfo.InvariantCulture);
        var target = rest[(slash + 1)..];
        var location = n <= 1 ? "/" + target : $"/redirect/{n - 1}/{target}";
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = location + context.Request.QueryString;
    }

    private static async Task ServeFileAsync(HttpContext context, TestFile file)
    {
        var request = context.Request;
        var response = context.Response;
        var isHead = HttpMethods.IsHead(request.Method);
        if (!isHead && !HttpMethods.IsGet(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (isHead && !file.AllowHead)
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (file.ForceStatus is { } forced)
        {
            response.StatusCode = forced;
            return;
        }

        var active = Interlocked.Increment(ref file.ActiveRequests);
        if (active > file.MaxConcurrentRequests && file.MaxConcurrentRequests > 0)
        {
            Interlocked.Decrement(ref file.ActiveRequests);
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.Headers.RetryAfter = "1";
            return;
        }

        file.TrackPeak(active);
        try
        {
            // Snapshot so a concurrent ChangeContent() can't tear a response.
            var seed = file.Seed;
            var size = file.Size;
            var etag = file.ETag;
            var lastModified = file.LastModified;

            response.ContentType = file.ContentType;
            if (file.SendValidators)
            {
                response.Headers.ETag = etag;
                response.Headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);
            }

            if (file.AdvertiseRanges && file.SupportsRanges)
            {
                response.Headers.AcceptRanges = "bytes";
            }

            if (file.ContentDisposition is { } disposition)
            {
                response.Headers.ContentDisposition = disposition;
            }

            long start = 0;
            var end = size - 1;
            var partial = false;
            var rangeHeader = request.Headers.Range.ToString();
            var honorRange = file.SupportsRanges && (!file.RangesOnlyForProbe || rangeHeader == "bytes=0-0");
            if (honorRange && TryParseRange(rangeHeader, size, out var rangeStart, out var rangeEnd, out var unsatisfiable)
                && IfRangeMatches(request.Headers.IfRange.ToString(), etag, lastModified))
            {
                if (unsatisfiable)
                {
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers.ContentRange = $"bytes */{size}";
                    return;
                }

                start = rangeStart;
                end = rangeEnd;
                partial = true;
            }

            var length = Math.Max(0, end - start + 1);
            response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            if (partial)
            {
                response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
            }

            if (file.SendContentLength)
            {
                response.ContentLength = length;
            }

            if (isHead)
            {
                return;
            }

            await WriteBodyAsync(context, file, seed, start, length).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref file.ActiveRequests);
        }
    }

    private static async Task WriteBodyAsync(HttpContext context, TestFile file, int seed, long start, long length)
    {
        const int chunk = 16 * 1024;
        var buffer = new byte[chunk];
        var dropAfter = file.DropAfterBytes is { } drop && Interlocked.Increment(ref file.DropCandidates) <= file.DropLimit
            ? Random.Shared.NextInt64(drop.Min, drop.Max + 1L)
            : long.MaxValue;
        var bytesPerSecond = file.BytesPerSecondByStart?.Invoke(start) ?? file.BytesPerSecond;
        var started = DateTime.UtcNow;
        long sent = 0;
        var ct = context.RequestAborted;

        while (sent < length && !ct.IsCancellationRequested)
        {
            var count = (int)Math.Min(chunk, length - sent);
            if (sent + count > dropAfter)
            {
                count = (int)Math.Max(0, dropAfter - sent);
            }

            if (count > 0)
            {
                file.Fill(seed, start + sent, buffer.AsSpan(0, count));
                try
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    return;
                }

                sent += count;
            }

            if (sent >= dropAfter)
            {
                context.Abort();
                return;
            }

            if (bytesPerSecond > 0)
            {
                var due = started + TimeSpan.FromSeconds((double)sent / bytesPerSecond);
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static bool IfRangeMatches(string ifRange, string etag, DateTimeOffset lastModified)
    {
        if (string.IsNullOrEmpty(ifRange))
        {
            return true;
        }

        if (ifRange.StartsWith('"') || ifRange.StartsWith("W/", StringComparison.Ordinal))
        {
            return string.Equals(ifRange, etag, StringComparison.Ordinal);
        }

        return DateTimeOffset.TryParse(ifRange, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            && date >= lastModified.AddSeconds(-1) && date <= lastModified.AddSeconds(1);
    }

    /// <summary>Parses a single <c>bytes=</c> range (a-b, a-, -n).</summary>
    private static bool TryParseRange(string header, long size, out long start, out long end, out bool unsatisfiable)
    {
        start = 0;
        end = size - 1;
        unsatisfiable = false;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return false;
        }

        var first = spec[..dash];
        var last = spec[(dash + 1)..];
        if (first.Length == 0)
        {
            if (!long.TryParse(last, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, size - suffix);
            end = size - 1;
            unsatisfiable = size == 0;
            return true;
        }

        if (!long.TryParse(first, out start))
        {
            return false;
        }

        end = last.Length == 0 ? size - 1 : Math.Min(size - 1, long.TryParse(last, out var e) ? e : size - 1);
        unsatisfiable = start >= size || end < start;
        return true;
    }
}
