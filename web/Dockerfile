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
FROM nginx:1.27-alpine

COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/index.html || exit 1
