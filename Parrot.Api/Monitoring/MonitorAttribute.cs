namespace Parrot.Api.Monitoring;

/// <summary>
/// Names and tunes how an endpoint is monitored. Every request is measured
/// whether or not it carries this; the attribute only gives it a stable name
/// and its own idea of "slow".
/// <code>
/// [Monitor(Area = "auth")]                              // on the controller
/// public class AuthController : ControllerBase
/// {
///     [Monitor("auth.login", SlowMs = 1500)]            // on an action
///     public Task&lt;...&gt; LoginAsync(...) { ... }
/// }
/// </code>
/// On a controller, <see cref="Area"/> prefixes every action's name and
/// <see cref="SlowMs"/> is the default for them; an action's own values win.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MonitorAttribute : Attribute
{
    public MonitorAttribute()
    {
    }

    public MonitorAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The operation name metrics and logs are grouped by, e.g. "auth.login".</summary>
    public string? Name { get; init; }

    /// <summary>Prefix for actions that do not set a <see cref="Name"/>: "auth" gives "auth.login".</summary>
    public string? Area { get; init; }

    /// <summary>Above this a request is logged as slow and counted in parrot.http.server.slow. 0 = the global default.</summary>
    public int SlowMs { get; init; }

    /// <summary>Leave the endpoint out of monitoring entirely (the monitoring endpoint itself, say).</summary>
    public bool Ignore { get; init; }
}
