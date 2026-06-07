# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer so NuGet restore stays cached across source-only edits.
COPY BridgeServer.csproj ./
RUN dotnet restore BridgeServer.csproj

COPY . .
RUN dotnet publish BridgeServer.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# One port serves /app (WS) + /mcp (MCP). Plain — TLS terminated upstream (WebStation / reverse proxy).
EXPOSE 5050

ENTRYPOINT ["dotnet", "BridgeServer.dll"]
