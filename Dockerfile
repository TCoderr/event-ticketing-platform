# syntax=docker/dockerfile:1
ARG NODE_IMAGE=node:24-alpine
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12

# --- SPA ---------------------------------------------------------------------
FROM ${NODE_IMAGE} AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
# İsteğe bağlı yerel bağlayıcı (rolldown) sessizce atlanırsa katman başarısız olur; bozuk kurulum önbelleğe alınmaz.
RUN npm ci --no-audit --no-fund && node -e "require('rolldown')"
COPY web/ ./
RUN npm run build

# --- API ---------------------------------------------------------------------
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Loca.Domain/Loca.Domain.csproj src/Loca.Domain/packages.lock.json src/Loca.Domain/
COPY src/Loca.Api/Loca.Api.csproj src/Loca.Api/packages.lock.json src/Loca.Api/
RUN dotnet restore src/Loca.Api/Loca.Api.csproj --locked-mode
COPY src/ src/
COPY --from=web /web/dist/ src/Loca.Api/wwwroot/
RUN dotnet publish src/Loca.Api/Loca.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# --- Runtime -----------------------------------------------------------------
FROM ${DOTNET_ASPNET_IMAGE} AS final
LABEL org.opencontainers.image.title="Loca" \
      org.opencontainers.image.authors="TCoderr" \
      org.opencontainers.image.vendor="TCoderr"
WORKDIR /app
# Data Protection anahtarları ve yerel TLS sertifikası için kalıcı dizin; root olmayan kullanıcıya ait.
RUN mkdir -p /var/lib/loca/keys && chown -R ${APP_UID}:${APP_UID} /var/lib/loca
COPY --from=build /app/publish ./
USER ${APP_UID}
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8443
ENTRYPOINT ["dotnet", "Loca.Api.dll"]
