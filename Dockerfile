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
COPY src/AudioCentral.Providers.Spotify/AudioCentral.Providers.Spotify.csproj src/AudioCentral.Providers.Spotify/
COPY src/AudioCentral.Providers.LocalFiles/AudioCentral.Providers.LocalFiles.csproj src/AudioCentral.Providers.LocalFiles/
COPY src/AudioCentral.Providers.Podcasts/AudioCentral.Providers.Podcasts.csproj src/AudioCentral.Providers.Podcasts/
COPY src/AudioCentral.Players.GoogleCast/AudioCentral.Players.GoogleCast.csproj src/AudioCentral.Players.GoogleCast/
COPY src/AudioCentral.Players.Remote/AudioCentral.Players.Remote.csproj src/AudioCentral.Players.Remote/
COPY src/AudioCentral.Players.Groups/AudioCentral.Players.Groups.csproj src/AudioCentral.Players.Groups/
COPY src/AudioCentral.Web/AudioCentral.Web.csproj src/AudioCentral.Web/
COPY src/AudioCentral.Server/AudioCentral.Server.csproj src/AudioCentral.Server/
RUN dotnet restore src/AudioCentral.Server/AudioCentral.Server.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/AudioCentral.Server/AudioCentral.Server.csproj -c Release -a $TARGETARCH --no-restore -o /app

# go-librespot decodes Spotify; one daemon runs per linked account.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS librespot
ARG TARGETARCH
ARG LIBRESPOT_VERSION=v0.10.5
RUN case "$TARGETARCH" in amd64) arch=x86_64 ;; arm64) arch=arm64 ;; *) echo "Unsupported architecture $TARGETARCH" >&2; exit 1 ;; esac \
    && mkdir /librespot \
    && curl -fsSL "https://github.com/devgianlu/go-librespot/releases/download/${LIBRESPOT_VERSION}/go-librespot_linux_${arch}.tar.gz" \
       | tar -xz -C /librespot

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg encodes Spotify audio; go-librespot only links libasound dynamically. Spotify Soloist is not bundled (Spotify
# does not allow redistributing it): AudioCentral downloads it when chosen. It needs libatomic and plays into a
# private PulseAudio daemon that ffmpeg records.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libasound2t64 libatomic1 pulseaudio pulseaudio-utils \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data \
    && chown app:app /data

COPY --from=librespot /librespot/go-librespot /usr/local/bin/go-librespot

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=4727 \
    AudioCentral__DataDirectory=/data

USER app
VOLUME /data
EXPOSE 4727

ENTRYPOINT ["dotnet", "AudioCentral.Server.dll"]
