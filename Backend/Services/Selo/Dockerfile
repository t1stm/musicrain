# Rooms, queue, clock and chat — no ffmpeg, no yt-dlp, no platform state. See
# SERVICE_SPLIT_PLAN.md and compose.yaml for how this fits the rest of the split.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Selo/Selo.csproj Services/Selo/
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
# Every project's PackageReference to JetBrains.Annotations lives here, so the props file has to
# be in the context before the restore or [UsedImplicitly] stops compiling.
COPY Directory.Build.props .
RUN dotnet restore Services/Selo/Selo.csproj

COPY Services/Selo/ Services/Selo/
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
RUN dotnet publish Services/Selo/Selo.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Selo.dll"]
