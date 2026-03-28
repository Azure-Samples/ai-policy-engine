FROM node:20-alpine AS node-build
WORKDIR /ui
COPY chargeback-ui/package*.json ./
RUN npm ci
COPY chargeback-ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY Chargeback.ServiceDefaults/Chargeback.ServiceDefaults.csproj Chargeback.ServiceDefaults/
COPY Chargeback.Api/Chargeback.Api.csproj Chargeback.Api/
RUN dotnet restore Chargeback.Api/Chargeback.Api.csproj

COPY Chargeback.ServiceDefaults/ Chargeback.ServiceDefaults/
COPY Chargeback.Api/ Chargeback.Api/
RUN dotnet publish Chargeback.Api/Chargeback.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
COPY --from=node-build /ui/dist ./wwwroot/
ENTRYPOINT ["dotnet", "Chargeback.Api.dll"]
