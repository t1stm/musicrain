# The Discord bot. Build context is the repo root (see compose.yaml), like every other service here,
# though the only project it references is Gaida.Admin, for the /Admin surface Oko pulls: it talks to
# Gaida.API over HTTP exactly like the frontend does, and DSharpPlus comes from nuget.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Gaida.Bot/Gaida.Bot.csproj Services/Gaida.Bot/
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
# Every project's PackageReference to JetBrains.Annotations lives here, so the props file has to
# be in the context before the restore or [UsedImplicitly] stops compiling.
COPY Directory.Build.props .
RUN dotnet restore Services/Gaida.Bot/Gaida.Bot.csproj

COPY Services/Gaida.Bot/ Services/Gaida.Bot/
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
RUN dotnet publish Services/Gaida.Bot/Gaida.Bot.csproj -c Release -o /app

# aspnet, because of the one thing it does serve: /Admin/snapshot, /Admin/requests and /Admin/events,
# for Oko to pull. Everything else the bot does is outbound — a gateway websocket and a UDP voice
# socket per guild. Without ADMIN_TOKEN nothing is mapped and this is an idle host on a closed port.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# No ffmpeg, no yt-dlp, no wvunpack: audio arrives as Ogg/Opus from /Audio/Download/Opus and is
# passed to Discord without being decoded. DSharpPlus.Voice does ship its own natives for linux-x64,
# and libsodium, libspeexdsp and libkoana out of that set load here — but two of them do not, and
# both are fatal rather than cosmetic:
#
#   libcrypto   DSharpPlus's OpenSslVersionCheck P/Invokes it by the unversioned name at startup.
#               .NET's own TLS needs OpenSSL so libcrypto.so.3 is here, but the unversioned symlink
#               lives in libssl-dev, which does not.
#
# The bundled libopus needs a pin to load at all — see the comment on DSharpPlus.Natives.Opus in
# Gaida.Bot.csproj. The ldd check below is what makes that a build failure rather than a
# DllNotFoundException on the first -p: it fails the build if any bundled native wants a symbol
# this image cannot provide.
RUN ln -sf /usr/lib/x86_64-linux-gnu/libcrypto.so.3 /usr/lib/x86_64-linux-gnu/libcrypto.so \
    && for native in /app/runtimes/linux-x64/native/*.so; do \
         ldd "$native" | grep -q "not found" && { echo "unresolved: $native"; ldd "$native"; exit 1; }; \
       done; true

# The accounts. Either the whole array inline here (what compose passes), or a file mounted at
# CONFIGURATION_LOCATION. Unset, the bot says so and exits instead of crash-looping.
ENV CONFIGURATION_LOCATION=/config/.env.json
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Gaida.Bot.dll"]
