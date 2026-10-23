# syntax=docker/dockerfile:1

# The web image: the built frontend, served by Caddy, which also puts the API behind the same address and gets the
# certificate. The build context is the repository root, because it also needs deploy/Caddyfile:
#   docker build -f frontend/Dockerfile -t chatnet-web .

# ##### Build #####
# npm ci installs exactly what the lockfile says and stops if it is out of step with package.json. The page talks to
# /api and /hubs on its own address (no VITE_API_URL or VITE_HUB_URL), which is what the proxy serves.
FROM node:24.22.1-alpine AS build
WORKDIR /app

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

# ##### Web #####
FROM caddy:2.11.7-alpine AS web
COPY --from=build /app/dist /srv
COPY deploy/Caddyfile /etc/caddy/Caddyfile
