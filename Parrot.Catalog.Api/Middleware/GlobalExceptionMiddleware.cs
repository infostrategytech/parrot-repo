using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Services;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Api.Middleware;

public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;
    private readonly CatalogMetrics _metrics;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger, IHostEnvironment environment, CatalogMetrics metrics)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, Translate(ex));
        }
    }

    private Exception Translate(Exception ex)
    {
        if (ex is DbUpdateException { InnerException: PostgresException pg })
        {
            return TryPg(pg) ?? ex;
        }

        if (ex is PostgresException direct)
        {
            return TryPg(direct) ?? ex;
        }

        return ex;
    }

    private Exception? TryPg(PostgresException pg)
    {
        try
        {
            return PgErrors.Translate(pg, _metrics);
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    private async Task WriteAsync(HttpContext context, Exception exception)
    {
        string traceId = context.TraceIdentifier;
        (HttpStatusCode status, string code, string message, IEnumerable<string>? errors) = exception switch
        {
            ValidationException v => (v.StatusCode, v.ErrorCode, v.Message, v.Errors),
            DomainException d => (d.StatusCode, d.ErrorCode, d.Message, null),
            BadHttpRequestException b when b.StatusCode == StatusCodes.Status413PayloadTooLarge => (HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE", "The request body exceeds 1 MB.", null),
            BadHttpRequestException b => ((HttpStatusCode)b.StatusCode, "BAD_REQUEST", "The request could not be read.", null),
            OperationCanceledException => (HttpStatusCode.RequestTimeout, "CANCELLED", "The request was cancelled.", null),
            _ => (HttpStatusCode.InternalServerError, "INTERNAL_ERROR", _environment.IsDevelopment() ? exception.Message : "An unexpected error occurred.", null),
        };

        if (status == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception. RequestId={RequestId} {Method} {Path}", traceId, context.Request.Method, context.Request.Path.Value);
        }
        else
        {
            _logger.LogWarning("Request failed {ErrorCode}: {Message}. RequestId={RequestId} {Method} {Path}", code, message, traceId, context.Request.Method, context.Request.Path.Value);
        }

        context.Response.Clear();
        context.Response.Headers[Security.RequestIdMiddleware.HeaderName] = traceId;
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse { ErrorCode = code, Message = message, Errors = errors, TraceId = traceId }, Options));
    }
}
