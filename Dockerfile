# Builds one of the example applications into an image. compose.yaml picks which one.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN dotnet publish "$PROJECT" --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG ASSEMBLY
# The applications choose their own port in appsettings.json.
ENV ASPNETCORE_HTTP_PORTS= \
    APP_ASSEMBLY=${ASSEMBLY}
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_ASSEMBLY.dll\""]
