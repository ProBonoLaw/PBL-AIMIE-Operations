# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY NuGet.Config ./
COPY src/Aimie.Shared/Aimie.Shared.csproj src/Aimie.Shared/
COPY src/Aimie.Operations/Aimie.Operations.csproj src/Aimie.Operations/
# Secret contents: Username=<github-user>;Password=<PAT-with-read:packages>;ValidAuthenticationTypes=Basic
RUN --mount=type=secret,id=github_packages_credentials,required=true \
    --mount=type=secret,id=github_packages_source,required=true \
    PACKAGES_SOURCE_URL="$(cat /run/secrets/github_packages_source)" \
    NuGetPackageSourceCredentials_github="$(cat /run/secrets/github_packages_credentials)" \
    dotnet restore src/Aimie.Operations/Aimie.Operations.csproj --configfile NuGet.Config
COPY src/ src/
RUN dotnet publish src/Aimie.Operations/Aimie.Operations.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Aimie.Operations.dll"]
