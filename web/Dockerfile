# The product site as a container: built with Node, served by nginx.
# Build from the repository root (the site takes the screenshots and icons from docs/ and build/):
#   docker build -f web/Dockerfile --build-arg SITE_URL=https://www.example.com -t nextcommander-web .
#   docker run -p 8080:8080 nextcommander-web

FROM node:22-alpine AS build
ARG SITE_URL
ARG REPO_URL
ENV SITE_URL=${SITE_URL} REPO_URL=${REPO_URL}
WORKDIR /src
COPY docs/screenshots docs/screenshots
COPY build/icons build/icons
COPY web web
RUN node web/build.mjs --check

FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/web/dist /usr/share/nginx/html
EXPOSE 8080
