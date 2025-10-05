FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS builder

# set version label
ARG TESLACAMPLAYER_VERSION

# install build dependencies
RUN apk add --no-cache \
    nodejs \
    npm

# copy local TeslaCamPlayer sources from this repo
WORKDIR /src
COPY TeslaCamPlayer/ /src/TeslaCamPlayer/

# build server (amd64)
WORKDIR /src/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server
RUN if [ -n "${TESLACAMPLAYER_VERSION}" ]; then \
      sed -i'' -e "s/<AssemblyVersion>[0-9.*]\+<\/AssemblyVersion>/<AssemblyVersion>${TESLACAMPLAYER_VERSION}<\/AssemblyVersion>/g" TeslaCamPlayer.BlazorHosted.Server.csproj; \
    fi && \
    dotnet restore && \
    dotnet publish -c Release -o /tmp/build --no-restore --self-contained true -r linux-x64 /p:PublishTrimmed=true /p:DefineConstants=DOCKER ${TESLACAMPLAYER_VERSION:+/p:AssemblyVersion=${TESLACAMPLAYER_VERSION}}

# build client assets and assemble output
WORKDIR /src/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client
RUN npm install && \
    npm install -g gulp && \
    gulp default && \
    rm -rf /tmp/build/lib && \
    mkdir -p /out/app/teslacamplayer/wwwroot && \
    cp -r /tmp/build/* /out/app/teslacamplayer/ && \
    cp -r wwwroot/css/ /out/app/teslacamplayer/wwwroot/css/

# runtime
FROM ghcr.io/imagegenius/baseimage-ubuntu:noble

# set version label
ARG TESLACAMPLAYER_VERSION
ARG BUILD_DATE
LABEL build_version="Version:- ${TESLACAMPLAYER_VERSION} Build-date:- ${BUILD_DATE}"
LABEL maintainer="megabitus98"

# environment settings
ENV ClipsRootPath=/media \
  CACHE_DATABASE_PATH=/config/clips.db \
  ASPNETCORE_URLS=http://+:5000 \
  TESLACAMPLAYER_VERSION=${TESLACAMPLAYER_VERSION} \
  BUILD_DATE=${BUILD_DATE}

RUN \
  echo "**** install packages ****" && \
  apt-get update && \
  apt-get install -y \
    ffmpeg && \
  echo "**** cleanup ****" && \
  apt-get autoremove -y && \
  apt-get clean && \
  rm -rf \
    /tmp/* \
    /var/lib/apt/lists/* \
    /var/tmp/*

COPY --from=builder /out/ /

# copy local files
COPY root/ /

# ports and volumes
EXPOSE 5000

VOLUME /config /media
