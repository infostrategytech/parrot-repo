using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Api.Monitoring;

namespace Test.Parrot.Api.Tests.Monitoring;

public class MonitoringTests
{
    [Monitor(Area = "things")]
    private sealed class ThingsController : ControllerBase
    {
        public IActionResult ListAsync() => Ok();

        [Monitor("things.custom", SlowMs = 5000)]
        public IActionResult Custom() => Ok();

        [Monitor(Ignore = true)]
        public IActionResult Hidden() => Ok();
    }

    private sealed class PlainController : ControllerBase
    {
        public IActionResult ResendVerificationEmailAsync() => Ok();
    }

    private static Endpoint EndpointFor<T>(string action) where T : ControllerBase
    {
        MethodInfo method = typeof(T).GetMethod(action)!;
        ControllerActionDescriptor descriptor = new ControllerActionDescriptor
        {
            ControllerName = typeof(T).Name.Replace("Controller", string.Empty),
            ActionName = action,
            MethodInfo = method,
            ControllerTypeInfo = typeof(T).GetTypeInfo(),
        };
        return new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(descriptor), action);
    }

    // ---- naming from the decorator ---------------------------------------

    [Fact]
    public void Controller_area_prefixes_the_kebab_cased_action()
    {
        MonitoringMiddleware.Descriptor d = MonitoringMiddleware.Build(EndpointFor<ThingsController>(nameof(ThingsController.ListAsync)), 1000);

        Assert.Equal("things.list", d.Operation);
        Assert.Equal(1000, d.SlowMs);
    }

    [Fact]
    public void Action_attribute_overrides_name_and_threshold()
    {
        MonitoringMiddleware.Descriptor d = MonitoringMiddleware.Build(EndpointFor<ThingsController>(nameof(ThingsController.Custom)), 1000);

        Assert.Equal("things.custom", d.Operation);
        Assert.Equal(5000, d.SlowMs);
    }

    [Fact]
    public void Undecorated_controller_falls_back_to_controller_and_action()
    {
        MonitoringMiddleware.Descriptor d = MonitoringMiddleware.Build(EndpointFor<PlainController>(nameof(PlainController.ResendVerificationEmailAsync)), 1000);

        Assert.Equal("plain.resend-verification-email", d.Operation);
    }

    [Fact]
    public void Ignore_skips_the_endpoint()
    {
        Assert.True(MonitoringMiddleware.Build(EndpointFor<ThingsController>(nameof(ThingsController.Hidden)), 1000).Ignore);
    }

    // ---- the middleware ----------------------------------------------------

    private static (MonitoringMiddleware Middleware, MonitoringStore Store) Create(RequestDelegate next, int defaultSlowMs = 1000)
    {
        ServiceCollection services = new ServiceCollection();
        services.AddMetrics();
        IMeterFactory meters = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        MonitoringStore store = new MonitoringStore(TimeProvider.System);
        MonitoringMiddleware middleware = new MonitoringMiddleware(
            next,
            new RequestMetrics(meters),
            store,
            Options.Create(new MonitoringOptions { DefaultSlowMs = defaultSlowMs }),
            NullLogger<MonitoringMiddleware>.Instance);
        return (middleware, store);
    }

    private static DefaultHttpContext Context(Endpoint? endpoint = null)
    {
        DefaultHttpContext context = new DefaultHttpContext();
        context.Request.Method = "POST";
        if (endpoint is not null) context.SetEndpoint(endpoint);
        return context;
    }

    [Fact]
    public async Task Records_the_status_the_client_got()
    {
        (MonitoringMiddleware middleware, MonitoringStore store) = Create(ctx => { ctx.Response.StatusCode = 429; return Task.CompletedTask; });

        await middleware.InvokeAsync(Context(EndpointFor<ThingsController>(nameof(ThingsController.ListAsync))));

        OperationSummary op = Assert.Single(store.Snapshot(5).Operations);
        Assert.Equal("things.list", op.Operation);
        Assert.Equal(1, op.StatusCodes[429]);
        Assert.Equal(0, op.Errors);
    }

    [Fact]
    public async Task An_escaping_exception_counts_as_a_500_and_still_propagates()
    {
        (MonitoringMiddleware middleware, MonitoringStore store) = Create(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Context(EndpointFor<ThingsController>(nameof(ThingsController.ListAsync)))));

        MonitoringSnapshot snapshot = store.Snapshot(5);
        Assert.Equal(1, snapshot.Overall.Errors);
        Assert.Equal(500, Assert.Single(snapshot.RecentErrors).StatusCode);
    }

    [Fact]
    public async Task A_request_over_its_threshold_is_slow()
    {
        (MonitoringMiddleware middleware, MonitoringStore store) = Create(async _ => await Task.Delay(40), defaultSlowMs: 10);

        await middleware.InvokeAsync(Context(EndpointFor<ThingsController>(nameof(ThingsController.ListAsync))));

        Assert.Equal(1, store.Snapshot(5).Overall.Slow);
    }

    [Fact]
    public async Task Unmatched_paths_share_one_bucket()
    {
        (MonitoringMiddleware middleware, MonitoringStore store) = Create(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });

        await middleware.InvokeAsync(Context());
        await middleware.InvokeAsync(Context());

        OperationSummary op = Assert.Single(store.Snapshot(5).Operations);
        Assert.Equal("unmatched", op.Operation);
        Assert.Equal(2, op.Requests);
    }

    [Fact]
    public async Task Ignored_endpoints_are_not_recorded()
    {
        (MonitoringMiddleware middleware, MonitoringStore store) = Create(_ => Task.CompletedTask);

        await middleware.InvokeAsync(Context(EndpointFor<ThingsController>(nameof(ThingsController.Hidden))));

        Assert.Empty(store.Snapshot(5).Operations);
    }

    // ---- the store -----------------------------------------------------------

    [Fact]
    public void Percentiles_interpolate_over_sorted_samples()
    {
        // 0..100: 101 samples, so each percentile lands exactly on a sample.
        double[] sorted = Enumerable.Range(0, 101).Select(i => (double)i).ToArray();

        Assert.Equal(50, MonitoringStore.Percentile(sorted, 0.50));
        Assert.Equal(95, MonitoringStore.Percentile(sorted, 0.95));
        Assert.Equal(99, MonitoringStore.Percentile(sorted, 0.99));
        Assert.Equal(12.5, MonitoringStore.Percentile([10, 15], 0.50)); // interpolates between neighbours
        Assert.Equal(0, MonitoringStore.Percentile([], 0.95));
    }

    [Fact]
    public void Samples_outside_the_window_are_left_out()
    {
        MonitoringStore store = new MonitoringStore(TimeProvider.System);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.Record(new RequestSample("op", "GET", 200, 10, false, Outcomes.Success, now.AddMinutes(-30), null));
        store.Record(new RequestSample("op", "GET", 503, 900, false, Outcomes.ServerError, now, "abc"));

        MonitoringSnapshot lastFive = store.Snapshot(5);
        MonitoringSnapshot lastHour = store.Snapshot(60);

        Assert.Equal(1, lastFive.Overall.Requests);
        Assert.Equal(1.0, lastFive.Overall.ErrorRate);
        Assert.Equal(2, lastHour.Overall.Requests);
        Assert.Equal("abc", Assert.Single(lastFive.RecentErrors).CorrelationId);
    }

    // ---- the endpoint ----------------------------------------------------------

    private static MonitoringController Controller(string? key, string environment)
    {
        Mock<IHostEnvironment> env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environment);
        return new MonitoringController(new MonitoringStore(TimeProvider.System), Options.Create(new MonitoringOptions { ApiKey = key }), env.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public void Endpoint_is_hidden_without_a_configured_key_outside_development()
    {
        Assert.IsType<NotFoundResult>(Controller(null, "Production").Get());
    }

    [Fact]
    public void Endpoint_refuses_a_wrong_key_and_answers_the_right_one()
    {
        MonitoringController controller = Controller("s3cret-key", "Production");

        Assert.IsType<NotFoundResult>(controller.Get(key: "wrong"));
        Assert.IsType<NotFoundResult>(controller.Get(key: null));
        Assert.IsType<OkObjectResult>(controller.Get(key: "s3cret-key"));
    }
}
