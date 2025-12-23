# syntax=docker/dockerfile:1.7

######## builder: .NET (Debian/glibc) ########
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-builder
ARG TESLACAMPLAYER_VERSION
ARG TARGETARCH
WORKDIR /src

# Restore with caching (global + HTTP cache)
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/*.csproj TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/*.csproj TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Shared/*.csproj TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Shared/
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=cache,target=/root/.local/share/NuGet/v3-cache \
    dotnet restore TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server

# Bring in full sources
COPY TeslaCamPlayer/ TeslaCamPlayer/
WORKDIR /src/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server

# Optional: bump AssemblyVersion if provided
RUN if [ -n "${TESLACAMPLAYER_VERSION}" ]; then \
      sed -i'' -e "s#<AssemblyVersion>[0-9.*]\\+</AssemblyVersion>#<AssemblyVersion>${TESLACAMPLAYER_VERSION}</AssemblyVersion>#g" TeslaCamPlayer.BlazorHosted.Server.csproj; \
    fi

# Map Docker arch -> .NET RID arch token (glibc)
ENV RID_PREFIX=linux
RUN if [ "$TARGETARCH" = "amd64" ]; then export RID=${RID_PREFIX}-x64; else export RID=${RID_PREFIX}-${TARGETARCH}; fi && \
    dotnet publish -c Release -o /tmp/publish \
      -r ${RID} --self-contained true \
      /p:EnableCompressionInSingleFile=true \
      /p:DebugType=none \
      /p:DefineConstants=DOCKER \
      ${TESLACAMPLAYER_VERSION:+/p:AssemblyVersion=${TESLACAMPLAYER_VERSION}}

######## builder: Node (Debian) ########
FROM node:20-bullseye-slim AS client-build
WORKDIR /src/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client

# Deterministic, CI-friendly install
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/package*.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund

# Build client assets
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/ ./
RUN npx gulp default
# Copy only the contents of wwwroot/css into /client-static/css (avoid nested css/css)
RUN mkdir -p /client-static/css && cp -r wwwroot/css/. /client-static/css/

######## runtime: keep linuxserver base (Ubuntu noble amd64) ########
FROM ghcr.io/imagegenius/baseimage-ubuntu:noble

ARG TESLACAMPLAYER_VERSION
ARG BUILD_DATE
LABEL build_version="Version:- ${TESLACAMPLAYER_VERSION} Build-date:- ${BUILD_DATE}"
LABEL maintainer="megabitus98"

ENV ClipsRootPath=/media \
    CACHE_DATABASE_PATH=/config/clips.db \
    ASPNETCORE_URLS=http://+:5000 \
    TESLACAMPLAYER_VERSION=${TESLACAMPLAYER_VERSION} \
    BUILD_DATE=${BUILD_DATE} \
    DEBIAN_FRONTEND=noninteractive \
    DOTNET_EnableDiagnostics=0

# ffmpeg minimal + cleanup in one layer
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Copy published server + client CSS
COPY --from=dotnet-builder /tmp/publish/ /app/teslacamplayer/
COPY --from=client-build   /client-static/css/ /app/teslacamplayer/wwwroot/css/

# Copy HUD rendering scripts
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/lib/ /app/teslacamplayer/lib/

# Install Python and Pillow for HUD renderer
RUN apt-get update && \
    apt-get install -y --no-install-recommends python3 python3-pip fonts-dejavu-core && \
    pip3 install --no-cache-dir --break-system-packages Pillow && \
    rm -rf /var/lib/apt/lists/* && \
    chmod +x /app/teslacamplayer/lib/hud_renderer.py

# Existing s6 init files
COPY root/ /

EXPOSE 5000
VOLUME ["/config", "/media"]
