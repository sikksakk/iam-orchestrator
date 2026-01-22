# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore
COPY iam-orchestrator.csproj .
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Install Docker CLI using static binary (more reliable than apt)
RUN apt-get update && \
    apt-get install -y curl && \
    curl -fsSL https://download.docker.com/linux/static/stable/x86_64/docker-27.4.1.tgz -o docker.tgz && \
    tar xzvf docker.tgz --strip 1 -C /usr/local/bin docker/docker && \
    rm docker.tgz && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "iam-orchestrator.dll"]
