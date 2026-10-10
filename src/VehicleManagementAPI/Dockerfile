FROM pitstop-dotnet-sdk-base:1.0 AS build-env
WORKDIR /src

# Build from the src/ context: this service now references
# Infrastructure.Messaging by project, so its source must be present.
COPY Directory.Packages.props global.json ./
COPY Infrastructure.Messaging/ Infrastructure.Messaging/
COPY VehicleManagementAPI/ VehicleManagementAPI/

WORKDIR /src/VehicleManagementAPI
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Build runtime image
FROM pitstop-dotnet-aspnet-base:1.0
WORKDIR /app
COPY --from=build-env /src/VehicleManagementAPI/out .

# Expose ports
EXPOSE 5000/tcp
ENV ASPNETCORE_URLS=http://*:5000
HEALTHCHECK --interval=30s --timeout=3s --retries=1 CMD curl --silent --fail http://localhost:5000/hc || exit 1

# Start
ENTRYPOINT ["dotnet", "Pitstop.VehicleManagementAPI.dll"]
