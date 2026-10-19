# syntax=docker/dockerfile:1
# The cover service. Keeps the library's album art in its own volume and serves it, resized on request
# by ImageSharp.Web. See compose.yaml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Snimka/Snimka.csproj Services/Snimka/
COPY ["Gaida Library/Gaida.Admin/Gaida.Admin.csproj", "Gaida Library/Gaida.Admin/"]
# Every project's PackageReference to JetBrains.Annotations lives in Directory.Build.props and every
# package version in Directory.Packages.props, so both have to be in the context before the restore.
COPY Directory.Build.props Directory.Packages.props ./
RUN dotnet restore Services/Snimka/Snimka.csproj

COPY Services/Snimka/ Services/Snimka/
COPY ["Gaida Library/Gaida.Admin/", "Gaida Library/Gaida.Admin/"]
# ImageSharp.Web 4 will not build without a Six Labors license key. It arrives as a BuildKit secret, so it
# is in this one step's environment and in no layer, no build argument and no image history.
RUN --mount=type=secret,id=sixlabors_license_key,env=SIXLABORS_LICENSE_KEY \
    dotnet publish Services/Snimka/Snimka.csproj -c Release -o /app

# The store's naming and refusals run with no volume and no port, so a broken store fails the build.
RUN cd /app && dotnet Snimka.dll --self-check

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV SNIMKA_DATA=/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "Snimka.dll"]
