FROM pitstop-dotnet-sdk-base:1.0 AS build-env
WORKDIR /app

# Install node and pnpm
ENV NODE_VERSION=22.x
ENV PNPM_VERSION=10
RUN apt-get -y update \
    && apt-get install -y curl \
    && curl -sL https://deb.nodesource.com/setup_${NODE_VERSION} | sh - \
    && apt-get install -y nodejs \
    && apt-get clean \
    && npm install -g pnpm@${PNPM_VERSION}

# Build from the src/ context: WebApp now references
# Infrastructure.Messaging by project, so its source must be present.
WORKDIR /src
COPY Directory.Packages.props global.json ./
COPY Infrastructure.Messaging/ Infrastructure.Messaging/
COPY WebApp/ WebApp/

WORKDIR /src/WebApp
RUN dotnet restore
RUN pnpm install
RUN pnpm run build
RUN dotnet publish -c Release -o out

# Build runtime image
FROM pitstop-dotnet-aspnet-base:1.0
WORKDIR /app
COPY --from=build-env /src/WebApp/out .

# Expose ports
EXPOSE 7005/tcp
ENV ASPNETCORE_URLS=http://*:7005
HEALTHCHECK --interval=30s --timeout=3s --retries=1 CMD curl --silent --fail http://localhost:7005/hc || exit 1

# Start
ENTRYPOINT ["dotnet", "Pitstop.WebApp.dll"]
