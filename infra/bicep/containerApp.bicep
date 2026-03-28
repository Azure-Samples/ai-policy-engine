@description('Name of the Container App')
param containerAppName string

@description('Location for the resources')
param location string

@description('Name of the Container App Environment')
param containerAppEnvName string

@description('Container image to deploy')
param containerImage string = 'mcr.microsoft.com/dotnet/aspnet:10.0'

@description('Redis cache name for connection')
param redisCacheName string

param subscriptionId string
param azureResourceGroup string

@description('Azure AI Service endpoint for deployment discovery')
param aiServiceEndpoint string = ''

@description('Application Insights connection string')
param appInsightsConnectionString string = ''

@description('Purview client app ID for Agent 365 integration')
param purviewClientAppId string = ''

@description('Entra ID tenant ID for JWT validation')
param entraIdTenantId string = ''

@description('Container App Entra ID app registration client ID (audience for APIM managed identity auth)')
param containerAppClientId string = ''

@description('Cosmos DB endpoint URL')
param cosmosEndpoint string = ''

@description('ACR login server (e.g. myacr.azurecr.io). Leave empty to pull from public registries.')
param acrLoginServer string = ''

@description('ACR admin username. Required when acrLoginServer is set.')
param acrUsername string = ''

@secure()
@description('ACR admin password. Required when acrLoginServer is set.')
param acrPassword string = ''

@description('Minimum number of replicas (0 for scale-to-zero)')
@minValue(0)
@maxValue(30)
param minReplicas int = 1

@description('Maximum number of replicas')
@minValue(1)
@maxValue(30)
param maxReplicas int = 10

resource redisEnterprise 'Microsoft.Cache/redisEnterprise@2025-04-01' existing = {
  name: redisCacheName
  scope: resourceGroup()
}

resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  properties: {
    zoneRedundant: false
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: !empty(acrLoginServer) ? [
        {
          server: acrLoginServer
          username: acrUsername
          passwordSecretRef: 'acr-password'
        }
      ] : []
      secrets: !empty(acrLoginServer) ? [
        {
          name: 'acr-password'
          value: acrPassword
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: containerAppName
          image: containerImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'AZURE_SUBSCRIPTION_ID'
              value: subscriptionId
            }
            {
              name: 'AZURE_RESOURCE_GROUP'
              value: azureResourceGroup
            }
            {
              name: 'REDIS_NAME'
              value: redisCacheName
            }
            {
              name: 'ConnectionStrings__redis'
              value: '${redisEnterprise.name}.${location}.redis.azure.net:10000,ssl=True,abortConnect=False'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'PURVIEW_CLIENT_APP_ID'
              value: purviewClientAppId
            }
            {
              name: 'AzureAd__TenantId'
              value: entraIdTenantId
            }
            {
              name: 'AzureAd__ClientId'
              value: containerAppClientId
            }
            {
              name: 'AzureAd__Audience'
              value: !empty(containerAppClientId) ? 'api://${containerAppClientId}' : ''
            }
            {
              name: 'ConnectionStrings__chargeback'
              value: cosmosEndpoint
            }
            {
              name: 'AZURE_AI_ENDPOINT'
              value: aiServiceEndpoint
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-scaling'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

output containerAppUrl string = containerApp.properties.configuration.ingress.fqdn
output containerAppId string = containerApp.id
output containerAppPrincipalId string = containerApp.identity.principalId
output containerAppEnvId string = containerAppEnv.id
