# Build context: repository root. docker build -f src/BlueDrive.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props BlueDrive.slnx ./
COPY src/BlueDrive.Domain/BlueDrive.Domain.csproj src/BlueDrive.Domain/
COPY src/BlueDrive.Application/BlueDrive.Application.csproj src/BlueDrive.Application/
COPY src/BlueDrive.Infrastructure/BlueDrive.Infrastructure.csproj src/BlueDrive.Infrastructure/
COPY src/BlueDrive.Worker/BlueDrive.Worker.csproj src/BlueDrive.Worker/
RUN dotnet restore src/BlueDrive.Worker/BlueDrive.Worker.csproj
COPY src/ src/
ARG GIT_SHA=dev
RUN dotnet publish src/BlueDrive.Worker/BlueDrive.Worker.csproj -c Release -o /app --no-restore /p:UseAppHost=false /p:GIT_SHA=${GIT_SHA}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg/ffprobe: video & audio thumbnails/metadata; poppler-utils: pdftoppm; postgresql-client: pg_dump/pg_restore for backups.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg poppler-utils postgresql-client fonts-noto-core libfontconfig1 curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ARG GIT_SHA=dev
ENV DOTNET_gcServer=0 \
    GIT_SHA=${GIT_SHA} \
    TZ=Asia/Ho_Chi_Minh
ENTRYPOINT ["dotnet", "BlueDrive.Worker.dll"]
