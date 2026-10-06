FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["MessageProcessor/MessageProcessor.csproj", "MessageProcessor/"]
COPY ["Parrot.Application/Parrot.Application.csproj", "Parrot.Application/"]
COPY ["Parrot.Domain/Parrot.Domain.csproj", "Parrot.Domain/"]
COPY ["Parrot.Infrastructure/Parrot.Infrastructure.csproj", "Parrot.Infrastructure/"]
RUN dotnet restore "MessageProcessor/MessageProcessor.csproj"

COPY . .
WORKDIR "/src/MessageProcessor"
RUN dotnet publish "MessageProcessor.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MessageProcessor.dll"]
