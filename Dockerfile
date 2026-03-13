FROM mcr.microsoft.com/dotnet/sdk:10.0 AS app-build
WORKDIR /src

COPY DiscordSummaryBot.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS libdave-build
ARG LIBDAVE_TAG=v1.1.1/cpp

RUN apt-get update && apt-get install -y --no-install-recommends \
    autoconf \
    automake \
    build-essential \
    ca-certificates \
    cmake \
    curl \
    git \
    libtool \
    nasm \
    ninja-build \
    perl \
    pkg-config \
    python3 \
    unzip \
    zip \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /tmp
RUN git clone --depth 1 --branch "${LIBDAVE_TAG}" https://github.com/discord/libdave.git

WORKDIR /tmp/libdave
RUN git submodule update --init --recursive

WORKDIR /tmp/libdave/cpp
RUN ./vcpkg/bootstrap-vcpkg.sh && make cclean && make shared

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    libicu74 \
    libopus0 \
    libsodium23 \
    libstdc++6 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=app-build /app/out ./
COPY --from=libdave-build /tmp/libdave/cpp/lib/libdave.so ./libdave.so

ENTRYPOINT ["./DiscordSummaryBot"]
