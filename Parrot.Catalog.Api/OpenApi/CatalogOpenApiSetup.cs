using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Parrot.Catalog.Api.OpenApi;

internal static class CatalogOpenApiSetup
{
    private const string BearerScheme = "bearer";

    private const string Description = """
        The Parrot catalog is the system of record for products, services, subscription
        plans and bundles a tenant offers. Three surfaces share one host:

        * **Write API** (`/api/catalog/admin/...`) for administrators: types, attribute
          definitions, categories, products, variants, offers, prices, availability,
          relationships, media; publish and retire.
        * **Read API** (`/api/catalog/...`) for the CRM backend: published data only,
          deterministic search over registry-validated filters, compare, ETags, cursors.
        * **AI tools** (`/api/catalog/ai/tools/...`) for the assistant: six narrow tools
          whose JSON schemas are served at `GET /api/catalog/ai/tools/schemas`.

        ## Authentication and tenancy

        Every request needs `Authorization: Bearer <JWT>` carrying `tenant_id` and a
        `scope` claim with one or more of `catalog:read`, `catalog:write`,
        `catalog:publish`, `catalog:admin`, `catalog:ai-tools`. The tenant is taken from
        the token only; it never appears in a path, body or query.

        ## Attributes and the registry

        Each product type defines its attributes. `GET /api/catalog/admin/types/{id}/schema`
        returns the JSON Schema a form should render. Unknown keys, wrong types and
        missing required keys are rejected with `400`. Search filters must name
        filterable keys; anything else is `400`.

        ## Errors

        Anything outside 2xx carries one envelope:

        ```json
        { "success": false, "errorCode": "VALIDATION_ERROR", "message": "...", "errors": ["..."], "traceId": "..." }
        ```
        """;

    internal static void AddCatalogOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new OpenApiInfo();
                document.Info.Title = "Parrot Catalog API";
                document.Info.Version = "v1";
                document.Info.Description = Description;
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "A JWT carrying tenant_id and scope claims, without the word \"Bearer\".",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor!.EndpointMetadata.OfType<IAuthorizeData>().Any()
                    || !context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
                {
                    operation.Security ??= new List<OpenApiSecurityRequirement>();
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document, null)] = new List<string>(),
                    });
                }

                return Task.CompletedTask;
            });
        });
    }
}
