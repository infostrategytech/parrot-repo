FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Parrot.Catalog.Relay/Parrot.Catalog.Relay.csproj", "Parrot.Catalog.Relay/"]
COPY ["Parrot.Catalog.Application/Parrot.Catalog.Application.csproj", "Parrot.Catalog.Application/"]
COPY ["Parrot.Catalog.Domain/Parrot.Catalog.Domain.csproj", "Parrot.Catalog.Domain/"]
COPY ["Parrot.Catalog.Infrastructure/Parrot.Catalog.Infrastructure.csproj", "Parrot.Catalog.Infrastructure/"]
COPY ["Parrot.Domain/Parrot.Domain.csproj", "Parrot.Domain/"]
RUN dotnet restore "Parrot.Catalog.Relay/Parrot.Catalog.Relay.csproj"
COPY . .
WORKDIR "/src/Parrot.Catalog.Relay"
RUN dotnet publish "Parrot.Catalog.Relay.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Parrot.Catalog.Relay.dll"]
