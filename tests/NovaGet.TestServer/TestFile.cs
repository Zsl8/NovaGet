namespace NovaGet.TestServer;

/// <summary>A file served by <see cref="TestHttpServer"/>. Properties may be changed while tests run.</summary>
public sealed class TestFile
{
    private int _seed;
    private long _size;

    public TestFile(string path, long size, int seed)
    {
        Path = path;
        _size = size;
        _seed = seed;
        ETag = $"\"v{seed}-{size}\"";
        LastModified = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(seed);
    }

    public string Path { get; }

    public long Size => Volatile.Read(ref _size);

    public int Seed => Volatile.Read(ref _seed);

    /// <summary>Honor Range requests (206). When false the full body is always sent with 200.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Send <c>Accept-Ranges: bytes</c>.</summary>
    public bool AdvertiseRanges { get; set; } = true;

    /// <summary>When false, HEAD returns 405.</summary>
    public bool AllowHead { get; set; } = true;

    /// <summary>When false, the body is sent chunked without Content-Length.</summary>
    public bool SendContentLength { get; set; } = true;

    public bool SendValidators { get; set; } = true;

    public string ETag { get; private set; }

    public DateTimeOffset LastModified { get; private set; }

    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Raw Content-Disposition header value, if any.</summary>
    public string? ContentDisposition { get; set; }

    /// <summary>Per-request throttle in bytes per second (0 = unlimited).</summary>
    public int BytesPerSecond { get; set; }

    /// <summary>If set, each response is aborted after a random number of bytes in this range.</summary>
    public (int Min, int Max)? DropAfterBytes { get; set; }

    /// <summary>Only the first this-many responses are dropped (default: all of them).</summary>
    public int DropLimit { get; set; } = int.MaxValue;

    internal int DropCandidates;

    /// <summary>Requests beyond this many concurrent ones get 503 (0 = unlimited).</summary>
    public int MaxConcurrentRequests { get; set; }

    /// <summary>Forces a status code for GET/HEAD (e.g. 403, 404, 500).</summary>
    public int? ForceStatus { get; set; }

    internal int ActiveRequests;

    /// <summary>Replaces the content (new seed/size) and its validators, as if the file changed on the server.</summary>
    public void ChangeContent(int newSeed, long? newSize = null)
    {
        Volatile.Write(ref _seed, newSeed);
        if (newSize is { } size)
        {
            Volatile.Write(ref _size, size);
        }

        ETag = $"\"v{newSeed}-{Size}\"";
        LastModified = LastModified.AddHours(1);
    }

    public byte[] Content() => ContentGenerator.Generate(Seed, Size);

    public string Sha256() => ContentGenerator.Sha256Hex(Seed, Size);
}
