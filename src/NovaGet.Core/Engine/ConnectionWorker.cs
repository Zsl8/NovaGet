using System.Buffers;
using System.Globalization;

namespace NovaGet.Core.Engine;

/// <summary>
/// One logical connection of a download. It takes a segment, requests its byte range, streams the body into
/// the temp file at the right offsets and retries transient failures with backoff. When its segment is done it
/// asks the job for more work, so the pooled keep-alive socket serves the next piece.
/// </summary>
internal sealed class ConnectionWorker
{
    private readonly DownloadJob _job;
    private readonly ITransferProtocol _protocol;
    private long _downloaded;
    private volatile string _info = "Connecting...";
    private int _receivedAny;

    public ConnectionWorker(int number, DownloadJob job, ITransferProtocol protocol)
    {
        Number = number;
        _job = job;
        _protocol = protocol;
    }

    public int Number { get; }

    /// <summary>Raised once, when this connection receives its first bytes.</summary>
    public event Action<ConnectionWorker>? FirstBytesReceived;

    public ConnectionProgress Snapshot() => new(Number, Interlocked.Read(ref _downloaded), _info);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var segment = _job.NextSegment(this);
            while (segment is not null)
            {
                bool keepGoing;
                try
                {
                    keepGoing = await DownloadSegmentAsync(segment, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _job.Map.Release(segment);
                }

                if (!keepGoing)
                {
                    // Closed because the server limits connections; the segment goes back to the pool.
                    _info = "Closed (server limits connections)";
                    return;
                }

                segment = _job.NextSegment(this);
            }

            _info = "Done";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _info = "Stopped";
            throw;
        }
        catch (DownloadException ex)
        {
            _info = "Error: " + ex.Message;
            throw;
        }
    }

    /// <summary>Downloads a segment. Returns false if this connection should close to respect a server limit.</summary>
    private async Task<bool> DownloadSegmentAsync(LiveSegment segment, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!segment.IsReceived)
        {
            try
            {
                _info = "Send GET...";
                var response = await _protocol.OpenAsync(_job.CreateRequest(segment), cancellationToken).ConfigureAwait(false);
                await using (response.ConfigureAwait(false))
                {
                    _job.ValidateResponse(segment, response);
                    _info = "Receiving data...";
                    var endOfStream = await ReceiveAsync(response.Body, segment, cancellationToken).ConfigureAwait(false);
                    if (endOfStream && segment.End < 0)
                    {
                        _job.Map.CompleteUnknownSize(segment);
                        return true;
                    }

                    if (endOfStream && !segment.IsReceived)
                    {
                        throw new DownloadException(DownloadErrorKind.Network, "The connection closed before the data was complete.");
                    }
                }

                attempt = 0;
            }
            catch (DownloadException ex) when (ex.IsTransient && !cancellationToken.IsCancellationRequested)
            {
                if (ex.SuggestsConnectionLimit && _job.TryYieldConnection(this, ex))
                {
                    return false;
                }

                attempt++;
                if (!_job.RegisterFailure(this, ex))
                {
                    throw;
                }

                var delay = _job.Options.RetryDelay(attempt);
                _info = string.Create(CultureInfo.InvariantCulture, $"Error: {ex.Message} Retrying in {Math.Ceiling(delay.TotalSeconds):0} sec...");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>Streams the body into the file. Returns true at end of stream, false when the segment's end was reached.</summary>
    private async Task<bool> ReceiveAsync(Stream body, LiveSegment segment, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(16 * 1024, _job.Options.WriteBufferSize));
        var filled = 0;
        var bufferOffset = segment.Received;
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (true)
            {
                if (filled == buffer.Length)
                {
                    await FlushAsync().ConfigureAwait(false);
                }

                readTimeout.CancelAfter(_job.Options.Timeout);
                var want = Math.Min(buffer.Length - filled, _job.MaxReadSize);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(filled, want), readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DownloadException(DownloadErrorKind.Timeout, $"No data received for {_job.Options.Timeout.TotalSeconds:0} seconds.");
                }
                catch (IOException ex)
                {
                    throw new DownloadException(DownloadErrorKind.Network, "The connection was interrupted.", inner: ex);
                }

                if (read == 0)
                {
                    return true;
                }

                var accepted = _job.Map.Accept(segment, read);
                if (accepted > 0)
                {
                    filled += accepted;
                    Interlocked.Add(ref _downloaded, accepted);
                    _job.OnBytesReceived(accepted);
                    if (Interlocked.Exchange(ref _receivedAny, 1) == 0)
                    {
                        FirstBytesReceived?.Invoke(this);
                    }

                    await _job.ThrottleAsync(accepted, cancellationToken).ConfigureAwait(false);
                }

                if (accepted < read || segment.IsReceived)
                {
                    // Our (possibly split-shortened) range is complete; the rest belongs to another connection.
                    return false;
                }
            }
        }
        finally
        {
            try
            {
                // Bytes already received are valid wherever we stopped (pause, error, split): keep them.
                if (filled > 0)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        async Task FlushAsync()
        {
            await _job.WriteAsync(buffer.AsMemory(0, filled), bufferOffset).ConfigureAwait(false);
            bufferOffset += filled;
            filled = 0;
            _job.Map.MarkWritten(segment, bufferOffset);
        }
    }
}
