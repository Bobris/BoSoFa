# Build from the repository root: docker build -f deploy/Dockerfile [--build-arg BRAND=qsf] .
FROM node:24-bookworm-slim AS web
WORKDIR /src/web
COPY web/ .
RUN corepack enable && pnpm install --frozen-lockfile && pnpm --filter @bosofa/web build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
# The build's brand (brands/<name>.props) names the daemon and the desktop app this server hands out.
ARG BRAND=bosofa
WORKDIR /src
COPY . .
RUN dotnet publish src/BoSoFa.Server -c Release -p:Brand=$BRAND -o /app && mkdir /data

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
