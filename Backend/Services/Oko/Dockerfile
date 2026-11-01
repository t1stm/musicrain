# The staff gateway. References no service: Oko speaks to them over HTTP like any other client, so this
# image does not rebuild when they change. It does reference Gaida.Admin, the contract for who may act.
#
# It refuses to start without Dom:Url, ADMIN_USERNAME and ADMIN_PASSWORD -- an admin panel with no door
# is worse than no admin panel, so a missing setting is a crash-loop and not a warning.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Oko/Oko.csproj Services/Oko/
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
# Every project's PackageReference to JetBrains.Annotations lives in Directory.Build.props and every
# package version in Directory.Packages.props, so both have to be in the context before the restore.
COPY Directory.Build.props Directory.Packages.props ./
RUN dotnet restore Services/Oko/Oko.csproj

COPY Services/Oko/ Services/Oko/
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
RUN dotnet publish Services/Oko/Oko.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Oko.dll"]
