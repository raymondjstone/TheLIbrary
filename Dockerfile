# Stage 1: Build React frontend
FROM node:22-alpine AS frontend
WORKDIR /app/client
COPY thelibrary.client/package.json thelibrary.client/package-lock.json ./
RUN npm ci
COPY thelibrary.client/ ./
RUN npm run build
RUN ls -la /app/client/dist/ && test -f /app/client/dist/index.html

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY TheLIbrary.Server/TheLIbrary.Server.csproj TheLIbrary.Server/
RUN dotnet restore TheLIbrary.Server/TheLIbrary.Server.csproj /p:SkipSpa=true
COPY TheLIbrary.Server/ TheLIbrary.Server/
RUN dotnet publish TheLIbrary.Server/TheLIbrary.Server.csproj -c Release -o /app/publish --no-restore /p:SkipSpa=true

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install Calibre so the reMarkable "Convert & send" path can run
# `ebook-convert` for non-EPUB/PDF sources (MOBI, AZW3, FB2, DOCX, …).
# Without this the container's `ebook-convert` shell-out fails with
# "No such file or directory" and the send button reports a conversion
# error every time.
#
# Two install paths:
#  • The Debian-packaged `calibre` (apt-get) — reliable, larger image,
#    pulls Qt/X11 deps even with --no-install-recommends.
#  • Calibre's official installer — latest version, similar dep footprint.
# Going with apt for reproducibility; the small version lag is fine for
# format conversion. If you need a slimmer image and don't use the
# reMarkable convert flow, drop this block and remove --build-arg below.
ARG INSTALL_CALIBRE=true
RUN if [ "$INSTALL_CALIBRE" = "true" ]; then \
        apt-get update \
        && apt-get install -y --no-install-recommends \
            calibre \
            libegl1 \
            libopengl0 \
            libxkbcommon0 \
            libxcb-cursor0 \
        && rm -rf /var/lib/apt/lists/* \
        && ebook-convert --version ; \
    fi

COPY --from=backend /app/publish .
COPY --from=frontend /app/client/dist /app/wwwroot
RUN echo "=== /app contents ===" && ls -la /app/ && echo "=== /app/wwwroot ===" && ls -la /app/wwwroot/ && test -f /app/wwwroot/index.html

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=5043
ENV ASPNETCORE_URLS=
EXPOSE 5043

ENTRYPOINT ["dotnet", "TheLIbrary.Server.dll"]
