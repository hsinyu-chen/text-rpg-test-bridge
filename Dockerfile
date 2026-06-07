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

# 5051 = MCP-over-HTTP (agent), 5050 = WS (app). Both plain — TLS terminated by nginx upstream.
EXPOSE 5050 5051

ENTRYPOINT ["dotnet", "BridgeServer.dll"]
