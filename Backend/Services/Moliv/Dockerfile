# The listening history. No ffmpeg, no yt-dlp, no platform state: it stores track IDs and the listening
# around them, and asks Dom who a bearer token belongs to. Its volume is real user data, like Dom's.
# See HISTORY_PLAN.md and compose.yaml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Moliv/Moliv.csproj Services/Moliv/
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
COPY ["Gaida Library/Gaida.Sqlite/Gaida.Sqlite.csproj", "Gaida Library/Gaida.Sqlite/"]
# Every project's PackageReference to JetBrains.Annotations lives here, so the props file has to
# be in the context before the restore or [UsedImplicitly] stops compiling.
COPY Directory.Build.props .
RUN dotnet restore Services/Moliv/Moliv.csproj

COPY Services/Moliv/ Services/Moliv/
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
COPY ["Gaida Library/Gaida.Sqlite/", "Gaida Library/Gaida.Sqlite/"]
RUN dotnet publish Services/Moliv/Moliv.csproj -c Release -o /app

# Against a temporary database, so a broken merge rule fails the build rather than somebody's history.
RUN cd /app && dotnet Moliv.dll --self-check

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Moliv.dll"]
