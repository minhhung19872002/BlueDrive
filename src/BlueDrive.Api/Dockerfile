# Build context: repository root. docker build -f src/BlueDrive.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props BlueDrive.slnx ./
COPY src/BlueDrive.Domain/BlueDrive.Domain.csproj src/BlueDrive.Domain/
COPY src/BlueDrive.Application/BlueDrive.Application.csproj src/BlueDrive.Application/
COPY src/BlueDrive.Infrastructure/BlueDrive.Infrastructure.csproj src/BlueDrive.Infrastructure/
COPY src/BlueDrive.Api/BlueDrive.Api.csproj src/BlueDrive.Api/
RUN dotnet restore src/BlueDrive.Api/BlueDrive.Api.csproj
COPY src/ src/
ARG GIT_SHA=dev
RUN dotnet publish src/BlueDrive.Api/BlueDrive.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false /p:GIT_SHA=${GIT_SHA}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# upgrade: security fixes released after the base image was built (Trivy blocks High/Critical, HSMT 3.4).
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && apt-get install -y --no-install-recommends curl fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ARG GIT_SHA=dev
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0 \
    GIT_SHA=${GIT_SHA} \
    TZ=Asia/Ho_Chi_Minh
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=5 CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "BlueDrive.Api.dll"]
