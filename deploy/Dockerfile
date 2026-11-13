# Build from the repository root: docker build -f deploy/Dockerfile [--build-arg VERSION=1.2.0] [--build-arg BRAND=qsf] .
FROM node:24-bookworm-slim AS web
WORKDIR /src/web
COPY web/ .
# The web app, and the desktop app's page, which the desktop builds below carry.
RUN corepack enable && pnpm install --frozen-lockfile && pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
# The build's brand (brands/<name>.props) names the daemon and the desktop app this server hands out.
ARG BRAND=bosofa
# The version of the server and of the daemons and desktop apps it carries; computers only ever update to a newer one.
ARG VERSION=0.1.0
WORKDIR /src
COPY . .
COPY --from=web /src/web/apps/desktop/dist web/apps/desktop/dist/
# Also builds the daemon and the desktop app for every platform into /app/bundle (src/BoSoFa.Server/ClientBundle.targets).
RUN dotnet publish src/BoSoFa.Server -c Release -p:Brand=$BRAND -p:Version=$VERSION -o /app && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=server /app .
COPY --from=web /src/web/apps/web/dist wwwroot/
# The node's state: on a single node the whole database, blobs and Data Protection keys (mount a volume here); in a
# cluster only a cache. Owned by the image's non-root user, so a new named volume is writable.
COPY --from=server --chown=1654:1654 /data /data
ENV BoSoFa__DataDirectory=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "bosofa-server.dll"]
