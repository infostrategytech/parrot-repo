using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Parrot.Api.OpenApi;

/// <summary>
/// The document served at /openapi/v1.json in Development. It holds the prose
/// the generator cannot infer: what this API is, how a session is obtained, and
/// which endpoints need one. Summaries and property wording live next to the
/// code they describe, as <c>EndpointSummary</c> and <c>Description</c>.
/// </summary>
internal static class OpenApiSetup
{
    private const string BearerScheme = "bearer";

    private const string DocumentDescription = """
        Parrot is an API for conversational commerce: connect WhatsApp or a
        Facebook page, keep the conversations in one place, and answer them.

        ## Authentication

        Sign-up is an email address and a password, nothing else. With email
        verification on — the default — `POST /api/Auth/register` answers
        `201` with no session and emails a six-digit code; the code becomes a
        session through `POST /api/Auth/verify-email`. With verification off,
        registration answers `200` and signs the caller straight in.

        Sign-in takes an email address **or** a phone number in the same
        field, so a person never has to say which one they are typing.

        A session is a bearer token. Send it as
        `Authorization: Bearer <token>`; the endpoints marked with a lock
        answer `401` without one. `expiresAt` on the response says how long it
        lasts.

        ## Errors

        Anything outside 2xx carries one envelope:

        ```json
        {
          "success": false,
          "errorCode": "SOME_CODE",
          "message": "The wording a person is meant to read.",
          "errors": null,
          "traceId": "…"
        }
        ```

        `message` is the sentence shown to the person and is kept stable;
        `errorCode` is the value to branch on. Endpoints that accept
        credentials are rate-limited per client address and answer `429` once
        the window is spent.
        """;

    internal static void AddParrotOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new OpenApiInfo();
                document.Info.Description = DocumentDescription;

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "The token from POST /api/Auth/login or POST /api/Auth/verify-email, without the word \"Bearer\".",
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                // Only where [Authorize] is declared, so sign-up, sign-in and
                // the SSO redirect keep reading as public.
                if (context.Description.ActionDescriptor!.EndpointMetadata.OfType<IAuthorizeData>().Any())
                {
                    operation.Security ??= new List<OpenApiSecurityRequirement>();
                    operation.Security.Add(
                        new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference(BearerScheme, context.Document, null)] = new List<string>(),
                        });
                }

                return Task.CompletedTask;
            });
        });
    }
}
