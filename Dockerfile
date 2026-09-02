# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore on its own layer: editing a .cs file must not re-download every package.
# .editorconfig travels too - EnforceCodeStyleInBuild reads it, and omitting it makes
# the container build diverge from the local one.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Whalebone.Records.Api/Whalebone.Records.Api.csproj                       src/Whalebone.Records.Api/
COPY src/Whalebone.Records.Application/Whalebone.Records.Application.csproj       src/Whalebone.Records.Application/
COPY src/Whalebone.Records.Infrastructure/Whalebone.Records.Infrastructure.csproj src/Whalebone.Records.Infrastructure/
RUN dotnet restore src/Whalebone.Records.Api/Whalebone.Records.Api.csproj

COPY src/ src/
# ContinuousIntegrationBuild=true promotes warnings to errors, so the quality bar
# travels into the image build rather than living only in CI.
RUN dotnet publish src/Whalebone.Records.Api/Whalebone.Records.Api.csproj \
        --no-restore -c Release -o /app \
        -p:UseAppHost=false \
        -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# The aspnet image ships neither curl nor wget. ~4 MB buys a container HEALTHCHECK,
# which is what makes `docker compose up --wait` an honest readiness signal instead
# of "the process started".
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

# Non-root, using the unprivileged account the base image already provides.
USER app

HEALTHCHECK --interval=5s --timeout=3s --start-period=20s --retries=12 \
    CMD curl -fsS http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Whalebone.Records.Api.dll"]
