FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["Parrot.Api/Parrot.Api.Rest.csproj", "Parrot.Api/"]
COPY ["Parrot.Application/Parrot.Application.csproj", "Parrot.Application/"]
COPY ["Parrot.Domain/Parrot.Domain.csproj", "Parrot.Domain/"]
COPY ["Parrot.Infrastructure/Parrot.Infrastructure.csproj", "Parrot.Infrastructure/"]
RUN dotnet restore "Parrot.Api/Parrot.Api.Rest.csproj"

COPY . .
WORKDIR "/src/Parrot.Api"
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
RUN dotnet publish "Parrot.Api.Rest.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Parrot.Api.Rest.dll"]
