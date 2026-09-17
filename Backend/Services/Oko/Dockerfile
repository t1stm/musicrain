# The admin panel. Deliberately references no other project: Oko speaks to the services over HTTP
# like any other client, so this image does not rebuild when they change. See ADMIN_PLAN.md.
#
# It refuses to start without ADMIN_USERNAME and ADMIN_PASSWORD -- an admin panel with no door is
# worse than no admin panel, so a missing password is a crash-loop and not a warning.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Services/Oko/Oko.csproj Services/Oko/
# Every project's PackageReference to JetBrains.Annotations lives here, so the props file has to
# be in the context before the restore or [UsedImplicitly] stops compiling.
COPY Directory.Build.props .
RUN dotnet restore Services/Oko/Oko.csproj

COPY Services/Oko/ Services/Oko/
RUN dotnet publish Services/Oko/Oko.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Oko.dll"]
