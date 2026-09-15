# The lyrics service. No ffmpeg, no yt-dlp — it fetches text and writes text. It does share the music
# library volume with gaida-local, read-write, which is why compose runs it as the same uid:gid.
# See LYRICS_PLAN.md and compose.yaml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Stih/Stih.csproj Services/Stih/
COPY ["Gaida Library/Gaida.Core/Gaida.Core.csproj", "Gaida Library/Gaida.Core/"]
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
RUN dotnet restore Services/Stih/Stih.csproj

COPY Services/Stih/ Services/Stih/
COPY ["Gaida Library/Gaida.Core/", "Gaida Library/Gaida.Core/"]
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
RUN dotnet publish Services/Stih/Stih.csproj -c Release -o /app

# The pure halves run with no volumes and no network, so a broken parser fails the build.
RUN cd /app && dotnet Stih.dll --self-check

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# lyrics-data is a named volume, and Docker seeds a fresh one's ownership from the image's directory.
# Without this the volume is root-owned, and the container -- which compose runs as LIBRARY_UID -- cannot
# write Lyrics.json into it. /music needs no equivalent: it is a bind mount the host user already owns.
ARG LIBRARY_UID=1000
ARG LIBRARY_GID=1000
RUN mkdir -p /lyrics && chown ${LIBRARY_UID}:${LIBRARY_GID} /lyrics

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Stih.dll"]
