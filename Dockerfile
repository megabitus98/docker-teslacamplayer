FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS builder

# set version label
ARG TESLACAMPLAYER_VERSION

# install build dependencies
RUN apk add --no-cache \
    binutils \
    nodejs \
    npm

# copy local TeslaCamPlayer sources from this repo
WORKDIR /src
COPY TeslaCamPlayer/ /src/TeslaCamPlayer/

# build server
WORKDIR /src/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server
RUN if [ -n "${TESLACAMPLAYER_VERSION}" ]; then \
      sed -i'' -e "s/<AssemblyVersion>[0-9.*]\+<\/AssemblyVersion>/<AssemblyVersion>${TESLACAMPLAYER_VERSION}<\/AssemblyVersion>/g" TeslaCamPlayer.BlazorHosted.Server.csproj; \
    fi && \
    dotnet restore && \
    dotnet publish -c Release -o /tmp/build --no-restore --self-contained true -r linux-musl-x64 \
      /p:PublishTrimmed=true \
      /p:TrimMode=link \
      /p:PublishSingleFile=true \
      /p:EnableCompressionInSingleFile=true \
      /p:IncludeNativeLibrariesForSelfExtract=false \
      /p:StripSymbols=true \
      /p:DebugType=None \
      /p:DefineConstants=DOCKER && \
    if [ -f /tmp/build/TeslaCamPlayer.BlazorHosted.Server ]; then \
      strip --strip-unneeded /tmp/build/TeslaCamPlayer.BlazorHosted.Server; \
    fi

# build client assets and assemble output
WORKDIR /src/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client
RUN npm install --no-audit --progress=false && \
    npx gulp default && \
    rm -rf /tmp/build/lib && \
    mkdir -p /out/app/teslacamplayer/wwwroot && \
    cp -r /tmp/build/* /out/app/teslacamplayer/ && \
    cp -r wwwroot/css/ /out/app/teslacamplayer/wwwroot/css/

# runtime
FROM ghcr.io/imagegenius/baseimage-alpine:3.20

# set version label
ARG BUILD_DATE
ARG VERSION
LABEL build_version="ImageGenius Version:- ${VERSION} Build-date:- ${BUILD_DATE}"
LABEL maintainer="megabitus98"

# environment settings
ENV ClipsRootPath=/media \
  CacheFilePath=/config/clips.json \
  ASPNETCORE_URLS=http://+:5000 \
  ENABLE_DELETE=true

RUN \
  echo "**** install packages ****" && \
  apk add --no-cache \
    icu-libs \
    ffmpeg && \
  echo "**** cleanup ****" && \
  rm -rf \
    /tmp/*

COPY --from=builder /out/ /

# copy local files
COPY root/ /

RUN find /app -name "*.pdb" -delete || true

# ports and volumes
EXPOSE 5000

VOLUME /config /media
