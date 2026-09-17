# Build context is the repo root (see compose.yaml) -- project-references Gaida.Core only. Every
# platform is a pod reached over HTTP now (HttpPlatform, Gaida.Core/Platforms/HttpPlatform.cs).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Services/Gaida.API/Gaida.API.csproj", "Services/Gaida.API/"]
COPY ["Gaida Library/Gaida.Core/Gaida.Core.csproj", "Gaida Library/Gaida.Core/"]
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
# Every project's PackageReference to JetBrains.Annotations lives here, so the props file has to
# be in the context before the restore or [UsedImplicitly] stops compiling.
COPY Directory.Build.props .
RUN dotnet restore "Services/Gaida.API/Gaida.API.csproj"

COPY ["Services/Gaida.API/", "Services/Gaida.API/"]
COPY ["Gaida Library/Gaida.Core/", "Gaida Library/Gaida.Core/"]
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
RUN dotnet publish "Services/Gaida.API/Gaida.API.csproj" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# FFmpegEncoder.cs shells out to ffmpeg on PATH for every /Audio/Download.
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gaida.API.dll"]
