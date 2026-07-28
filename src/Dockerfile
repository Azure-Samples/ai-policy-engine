FROM node:20-alpine AS node-build
WORKDIR /ui
COPY src/aipolicyengine-ui/package*.json ./
RUN npm ci
COPY src/aipolicyengine-ui/ ./
RUN npm run build -- --outDir /ui/dist --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Directory.Packages.props ./
COPY src/AIPolicyEngine.ServiceDefaults/AIPolicyEngine.ServiceDefaults.csproj AIPolicyEngine.ServiceDefaults/
COPY src/AIPolicyEngine.Api/AIPolicyEngine.Api.csproj AIPolicyEngine.Api/
RUN dotnet restore AIPolicyEngine.Api/AIPolicyEngine.Api.csproj

COPY src/AIPolicyEngine.ServiceDefaults/ AIPolicyEngine.ServiceDefaults/
COPY src/AIPolicyEngine.Api/ AIPolicyEngine.Api/
# Stage the SPA build into the API's wwwroot before publish so the dotnet
# static web assets manifest includes the SPA. Without this, UseStaticFiles
# uses an empty manifest and 404s on /index.html etc.
COPY --from=node-build /ui/dist/ AIPolicyEngine.Api/wwwroot/
RUN dotnet publish AIPolicyEngine.Api/AIPolicyEngine.Api.csproj -c Release -o /app/publish --no-restore -p:SkipSpaBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
# Copy APIM policy templates so TemplateLibraryService can find them at
# {ContentRootPath}/policies/templates → /app/policies/templates
COPY policies/templates/ policies/templates/
USER $APP_UID
ENTRYPOINT ["dotnet", "AIPolicyEngine.Api.dll"]
