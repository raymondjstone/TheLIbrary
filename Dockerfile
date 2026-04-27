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
COPY --from=backend /app/publish .
COPY --from=frontend /app/client/dist /app/wwwroot
RUN echo "=== /app contents ===" && ls -la /app/ && echo "=== /app/wwwroot ===" && ls -la /app/wwwroot/ && test -f /app/wwwroot/index.html

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=5043
ENV ASPNETCORE_URLS=
EXPOSE 5043

ENTRYPOINT ["dotnet", "TheLIbrary.Server.dll"]
