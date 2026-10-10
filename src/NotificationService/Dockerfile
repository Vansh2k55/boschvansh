FROM pitstop-dotnet-sdk-base:1.0 AS build-env
WORKDIR /src

# Build from the src/ context: this service now references
# Infrastructure.Messaging by project, so its source must be present.
COPY Directory.Packages.props global.json ./
COPY Infrastructure.Messaging/ Infrastructure.Messaging/
COPY NotificationService/ NotificationService/

WORKDIR /src/NotificationService
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Build runtime image
FROM pitstop-dotnet-runtime-base:1.0
WORKDIR /app
COPY --from=build-env /src/NotificationService/out .

# Start
ENTRYPOINT ["dotnet", "Pitstop.NotificationService.dll"]
