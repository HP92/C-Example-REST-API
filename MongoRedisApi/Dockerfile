# ── Stage 1: build ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj first to leverage Docker layer caching for restore
COPY ["MongoRedisApi.csproj", "."]
RUN dotnet restore "MongoRedisApi.csproj"

# Copy everything else and publish a self-contained release build
COPY . .
RUN dotnet publish "MongoRedisApi.csproj" \
      -c Release \
      -o /app/publish \
      /p:UseAppHost=false

# ── Stage 2: runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Listen on HTTP only (TLS is typically terminated at the ingress/reverse-proxy)
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "MongoRedisApi.dll"]
