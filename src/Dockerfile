FROM node:20-alpine AS node-build
WORKDIR /ui
COPY aipolicyengine-ui/package*.json ./
RUN npm ci
COPY aipolicyengine-ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY AIPolicyEngine.ServiceDefaults/AIPolicyEngine.ServiceDefaults.csproj AIPolicyEngine.ServiceDefaults/
COPY AIPolicyEngine.Api/AIPolicyEngine.Api.csproj AIPolicyEngine.Api/
RUN dotnet restore AIPolicyEngine.Api/AIPolicyEngine.Api.csproj

COPY AIPolicyEngine.ServiceDefaults/ AIPolicyEngine.ServiceDefaults/
COPY AIPolicyEngine.Api/ AIPolicyEngine.Api/
RUN dotnet publish AIPolicyEngine.Api/AIPolicyEngine.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
COPY --from=node-build /ui/dist ./wwwroot/
ENTRYPOINT ["dotnet", "AIPolicyEngine.Api.dll"]
