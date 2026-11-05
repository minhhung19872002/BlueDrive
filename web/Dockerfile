# Build context: repository root (see deploy/docker-compose.yml: context "..", dockerfile "web/Dockerfile").
#   docker build -f web/Dockerfile -t bluedrive/web .

# ---------- Build stage ----------
FROM node:22-alpine AS build
WORKDIR /app

COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./
RUN npm run build

# ---------- Runtime stage ----------
# nginx stable branch; apk upgrade picks up Alpine security fixes released after the base image (Trivy gate in CI).
FROM nginx:1.31-alpine
RUN apk upgrade --no-cache

COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/index.html || exit 1
