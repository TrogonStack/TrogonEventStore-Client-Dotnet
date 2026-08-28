ARG BUILDER_IMG=mcr.microsoft.com/dotnet/sdk:10.0-noble
ARG RUNNER_IMG=mcr.microsoft.com/dotnet/runtime:10.0-noble
ARG APP_NAME="secure-with-tls"
ARG BUILD_CONFIGURATION=Release

#---------------------------------------------
# Prepare runtime image
#---------------------------------------------
FROM $RUNNER_IMG AS run
ARG APP_NAME

# copy pregenerated certificates into usr local CA location
COPY samples/secure-with-tls/certs/ca/ca.crt /usr/local/share/ca-certificates/eventstoredb_ca.crt

# set permissions
RUN chmod 644 /usr/local/share/ca-certificates/eventstoredb_ca.crt

# install certificates
RUN update-ca-certificates

# configure entrypoint
WORKDIR /app
ENV ENTRYPOINT "dotnet ${APP_NAME}.dll"
ENTRYPOINT $ENTRYPOINT

#---------------------------------------------
# Build application
#---------------------------------------------
FROM $BUILDER_IMG AS build
ARG APP_NAME
ARG BUILD_CONFIGURATION

WORKDIR /src

COPY Directory.Build.props Directory.Build.targets LICENSE.md README.md ./
COPY src ./src
COPY samples/Directory.Build.props ./samples/
COPY samples/${APP_NAME} ./samples/${APP_NAME}

ENV PROJECT_FILE_PATH=samples/$APP_NAME/$APP_NAME.csproj

RUN dotnet restore $PROJECT_FILE_PATH --verbosity minimal

RUN dotnet publish $PROJECT_FILE_PATH \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:MinVerSkip=true \
    /p:EnableSourceControlManagerQueries=false \
    /p:EnableSourceLink=false

#---------------------------------------------
# Create final runtime image
#---------------------------------------------
FROM run AS app
COPY --from=build /app/publish .
