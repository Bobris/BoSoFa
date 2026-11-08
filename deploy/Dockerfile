# Build from the repository root: docker build -f deploy/Dockerfile .
FROM node:24-bookworm-slim AS web
WORKDIR /src/web
COPY web/ .
RUN corepack enable && pnpm install --frozen-lockfile && pnpm --filter @bosofa/web build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY . .
RUN dotnet publish src/BoSoFa.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=server /app .
COPY --from=web /src/web/apps/web/dist wwwroot/
EXPOSE 8080
ENTRYPOINT ["dotnet", "bosofa-server.dll"]
