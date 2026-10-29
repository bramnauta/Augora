# syntax=docker/dockerfile:1

# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -t audiocentral .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first so the package layer is cached between source changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AudioCentral.Contracts/AudioCentral.Contracts.csproj src/AudioCentral.Contracts/
COPY src/AudioCentral.Core/AudioCentral.Core.csproj src/AudioCentral.Core/
COPY src/AudioCentral.ServiceDefaults/AudioCentral.ServiceDefaults.csproj src/AudioCentral.ServiceDefaults/
COPY src/AudioCentral.Providers.TuneIn/AudioCentral.Providers.TuneIn.csproj src/AudioCentral.Providers.TuneIn/
COPY src/AudioCentral.Players.GoogleCast/AudioCentral.Players.GoogleCast.csproj src/AudioCentral.Players.GoogleCast/
COPY src/AudioCentral.Players.Remote/AudioCentral.Players.Remote.csproj src/AudioCentral.Players.Remote/
COPY src/AudioCentral.Web/AudioCentral.Web.csproj src/AudioCentral.Web/
COPY src/AudioCentral.Server/AudioCentral.Server.csproj src/AudioCentral.Server/
RUN dotnet restore src/AudioCentral.Server/AudioCentral.Server.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/AudioCentral.Server/AudioCentral.Server.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg is used for transcoding (Spotify, local files); radio relays without it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data \
    && chown app:app /data

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=4727 \
    AudioCentral__DataDirectory=/data

USER app
VOLUME /data
EXPOSE 4727

ENTRYPOINT ["dotnet", "AudioCentral.Server.dll"]
