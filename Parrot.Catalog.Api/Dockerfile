# Non-root, read-only root filesystem compatible: the app writes nothing to disk.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# curl is only for the container health check (same approach as Parrot.Api/Dockerfile).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    COMPlus_EnableDiagnostics=0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Parrot.Catalog.Api/Parrot.Catalog.Api.csproj", "Parrot.Catalog.Api/"]
COPY ["Parrot.Catalog.Application/Parrot.Catalog.Application.csproj", "Parrot.Catalog.Application/"]
COPY ["Parrot.Catalog.Domain/Parrot.Catalog.Domain.csproj", "Parrot.Catalog.Domain/"]
COPY ["Parrot.Catalog.Infrastructure/Parrot.Catalog.Infrastructure.csproj", "Parrot.Catalog.Infrastructure/"]
COPY ["Parrot.Domain/Parrot.Domain.csproj", "Parrot.Domain/"]
RUN dotnet restore "Parrot.Catalog.Api/Parrot.Catalog.Api.csproj"
COPY . .
WORKDIR "/src/Parrot.Catalog.Api"
RUN dotnet publish "Parrot.Catalog.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Parrot.Catalog.Api.dll"]
