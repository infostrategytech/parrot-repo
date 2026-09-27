using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Parrot.Api.Monitoring;

namespace Parrot.Api.Controllers;

/// <summary>
/// A JSON view of the last hour: overall and per-operation request rate,
/// p50/p95/p99 latency, error rate, slow count, status codes, and the most
/// recent failures with their correlation ids. Guarded by the
/// X-Monitoring-Key header; without a configured key it does not exist
/// outside Development.
/// </summary>
[ApiController]
[Route("api/monitoring")]
[ApiExplorerSettings(IgnoreApi = true)]
[Monitor(Ignore = true)]
public sealed class MonitoringController : ControllerBase
{
    private readonly MonitoringStore _store;
    private readonly MonitoringOptions _options;
    private readonly IHostEnvironment _env;

    public MonitoringController(MonitoringStore store, IOptions<MonitoringOptions> options, IHostEnvironment env)
    {
        _store = store;
        _options = options.Value;
        _env = env;
    }

    [HttpGet]
    public IActionResult Get([FromQuery] int minutes = 15, [FromHeader(Name = "X-Monitoring-Key")] string? key = null)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            if (!_env.IsDevelopment()) return NotFound();
        }
        else if (!KeyMatches(key, _options.ApiKey))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(_store.Snapshot(minutes));
    }

    // Hashing first gives both sides the same length, so the comparison time
    // says nothing about how much of the key was right.
    private static bool KeyMatches(string? supplied, string expected) =>
        supplied is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
