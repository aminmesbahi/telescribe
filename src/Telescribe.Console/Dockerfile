# Build context: the "src" folder (the one that contains Telescribe.slnx), e.g.
#   docker build -f Telescribe.Console/Dockerfile -t telescribe src
# The first login asks for a verification code, so run the container interactively:
#   docker run -it -v telescribe-data:/app/exports telescribe

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:11.0-preview AS base
WORKDIR /app
# Exports and the Telegram session file are written to the working directory, so it must be writable.
RUN mkdir -p /app/exports && chown -R $APP_UID /app
USER $APP_UID


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["Telescribe.Console/Telescribe.Console.csproj", "Telescribe.Console/"]
COPY ["Telescribe.Core/Telescribe.Core.csproj", "Telescribe.Core/"]
RUN dotnet restore "./Telescribe.Console/Telescribe.Console.csproj"
COPY . .
WORKDIR "/src/Telescribe.Console"
RUN dotnet build "./Telescribe.Console.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Telescribe.Console.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
VOLUME ["/app/exports"]
ENTRYPOINT ["dotnet", "Telescribe.Console.dll"]
