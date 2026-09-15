# Build context is the repo root (see compose.yaml) -- project-references Gaida.Core and the
# MusicDatabase platform library.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Platforms/Gaida.Pods.MusicDatabase/Gaida.Pods.MusicDatabase.csproj", "Platforms/Gaida.Pods.MusicDatabase/"]
COPY ["Gaida Library/Gaida.Core/Gaida.Core.csproj", "Gaida Library/Gaida.Core/"]
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
COPY ["Platforms/Gaida.Platforms.MusicDatabase/Gaida.Platforms.MusicDatabase.csproj", "Platforms/Gaida.Platforms.MusicDatabase/"]
RUN dotnet restore "Platforms/Gaida.Pods.MusicDatabase/Gaida.Pods.MusicDatabase.csproj"

COPY ["Platforms/Gaida.Pods.MusicDatabase/", "Platforms/Gaida.Pods.MusicDatabase/"]
COPY ["Gaida Library/Gaida.Core/", "Gaida Library/Gaida.Core/"]
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
COPY ["Platforms/Gaida.Platforms.MusicDatabase/", "Platforms/Gaida.Platforms.MusicDatabase/"]
RUN dotnet publish "Platforms/Gaida.Pods.MusicDatabase/Gaida.Pods.MusicDatabase.csproj" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# MusicManager.Manager/MediaInfo.cs shells out to ffprobe to read audio tags; MusicGetter.cs shells
# out to wvunpack, the only decoder here that reads a .wvc WavPack correction file.
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg wavpack \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gaida.Pods.MusicDatabase.dll"]
