using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Parrot.Application.Integrations;

/// <summary>
/// One log line for every call to an outside service, under the "third_party." events: which
/// service, the method and path, the status and how long it took. Only the path is written: the
/// query, headers and bodies can carry tokens or what a person said, so they never reach the log.
/// </summary>
/// <remarks>
/// A stream returns at its headers, so its time is the wait for the first byte, not the whole read.
/// </remarks>
public sealed class ThirdPartyCallLogger(string service, ILogger<ThirdPartyCallLogger> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string method = request.Method.Method;
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        long started = Stopwatch.GetTimestamp();
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            int status = (int)response.StatusCode;
            logger.Log(
                response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning,
                "third_party.call Service={Service} Method={Method} Path={Path} Status={Status} ElapsedMs={ElapsedMs}",
                service,
                method,
                path,
                status,
                ElapsedMs(started));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "third_party.cancelled Service={Service} Method={Method} Path={Path} ElapsedMs={ElapsedMs}",
                service,
                method,
                path,
                ElapsedMs(started));
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable, refused or timed out: the type and message say which, never a body. The
            // innermost error is the real reason ("see inner exception" says nothing on its own).
            Exception cause = ex;
            while (cause.InnerException is not null) cause = cause.InnerException;
            logger.LogWarning(
                "third_party.failed Service={Service} Method={Method} Path={Path} ElapsedMs={ElapsedMs} Error={Error}: {Reason} Cause={Cause}: {CauseReason}",
                service,
                method,
                path,
                ElapsedMs(started),
                ex.GetType().Name,
                ex.Message,
                cause.GetType().Name,
                cause.Message);
            throw;
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
