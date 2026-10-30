# syntax=docker/dockerfile:1

# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -t augora .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first so the package layer is cached between source changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Augora.Contracts/Augora.Contracts.csproj src/Augora.Contracts/
COPY src/Augora.Core/Augora.Core.csproj src/Augora.Core/
COPY src/Augora.ServiceDefaults/Augora.ServiceDefaults.csproj src/Augora.ServiceDefaults/
COPY src/Augora.Providers.TuneIn/Augora.Providers.TuneIn.csproj src/Augora.Providers.TuneIn/
COPY src/Augora.Providers.Spotify/Augora.Providers.Spotify.csproj src/Augora.Providers.Spotify/
COPY src/Augora.Providers.LocalFiles/Augora.Providers.LocalFiles.csproj src/Augora.Providers.LocalFiles/
COPY src/Augora.Providers.Podcasts/Augora.Providers.Podcasts.csproj src/Augora.Providers.Podcasts/
COPY src/Augora.Players.GoogleCast/Augora.Players.GoogleCast.csproj src/Augora.Players.GoogleCast/
COPY src/Augora.Players.Remote/Augora.Players.Remote.csproj src/Augora.Players.Remote/
COPY src/Augora.Players.Groups/Augora.Players.Groups.csproj src/Augora.Players.Groups/
COPY src/Augora.Client/Augora.Client.csproj src/Augora.Client/
COPY src/Augora.Web/Augora.Web.csproj src/Augora.Web/
COPY src/Augora.Server/Augora.Server.csproj src/Augora.Server/
RUN dotnet restore src/Augora.Server/Augora.Server.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Augora.Server/Augora.Server.csproj -c Release -a $TARGETARCH --no-restore -o /app

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
# does not allow redistributing it): Augora downloads it when chosen. It needs libatomic and plays into a
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
    Augora__DataDirectory=/data

USER app
VOLUME /data
EXPOSE 4727

ENTRYPOINT ["dotnet", "Augora.Server.dll"]
