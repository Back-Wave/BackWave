# The live demo image for demo.backwave.app. The build context is the REPO ROOT, because the demo
# references many src/* projects and the shared Directory.Build.* files:
#
#   docker build -f samples/BackWave.Demo/Dockerfile -t backwave-demo .
#   docker run --rm -p 8080:8080 backwave-demo
#
# then open http://localhost:8080/. Set the Pro licence with `-e BackWave__ProLicense=<key>` to clear
# the unlicensed-Pro notice. The SQLite file lives inside the container and is intentionally ephemeral:
# the deployment recycles hourly (see fly.toml / the demo-recycle workflow) and re-seeds on boot.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the whole repo (source + .git for MinVer + Directory.Build.*), then restore and publish just
# the demo. A .dockerignore at the repo root keeps bin/obj and other noise out of the context.
COPY . .
RUN dotnet restore samples/BackWave.Demo/BackWave.Demo.csproj
RUN dotnet publish samples/BackWave.Demo/BackWave.Demo.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Bind to all interfaces on 8080 (overrides the localhost dev URL in appsettings.json).
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "BackWave.Demo.dll"]
