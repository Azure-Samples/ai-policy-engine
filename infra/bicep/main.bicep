@description('Name of the API Management instance.')
param apimInstanceName string
param oaiApiName string
param funcApiName string
@description('Name of the Container App for the .NET chargeback API.')
param containerAppName string = 'ca-chargeback'
@description('Name of the Container App Environment.')
param containerAppEnvName string = 'cae-chargeback'
@description('Container image for the chargeback API.')
param containerImage string = 'mcr.microsoft.com/dotnet/aspnet:10.0'
param keyVaultName string
param redisCacheName string
param cosmosAccountName string
param logAnalyticsWorkspaceName string

@description('Name of the Application Insights resource.')
param appInsightsName string = 'ai-chargeback'

@description('Display name for the Azure Monitor workbook dashboard.')
param workbookDisplayName string = 'Chargeback Log Analytics Dashboard'

@description('Purview client app ID for Agent 365 integration (leave empty to skip).')
param purviewClientAppId string = ''

@description('Entra ID tenant ID for JWT validation.')
param entraIdTenantId string = ''

@description('Container App Entra ID app registration client ID. APIM uses this as the audience when acquiring managed identity tokens for the Container App.')
param containerAppClientId string = ''

@description('ACR login server for Container App image pull (e.g. myacr.azurecr.io).')
param acrLoginServer string = ''

@description('ACR resource name for AcrPull role assignment. Required when acrLoginServer is set.')
param acrName string = ''

@minLength(3)
@maxLength(24)
param storageAccountName string

@minLength(1)
@maxLength(64)
@description('Name of the workload which is used to generate a short unique hash used in all resources.')
param workloadName string

@description('Optional explicit Azure AI Services account name (leave empty to auto-generate).')
param aiServiceName string = ''

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Tags for all resources.')
param tags object = {
  WorkloadName: workloadName
  Environment: 'Dev'
}

@description('Deploy the JWT-authenticated OpenAI API endpoint.')
param enableJwt bool = true

@description('Deploy the subscription-key-authenticated OpenAI API endpoint.')
param enableKeys bool = true

var abbrs = loadJsonContent('./abbrs.json')
var roles = loadJsonContent('./roles.json')
var resourceToken = toLower(uniqueString(subscription().id, workloadName, location))
var resolvedAiServiceName = empty(aiServiceName) ? '${abbrs.ai.aiServices}${resourceToken}' : toLower(aiServiceName)


module keyVault './keyVault.bicep' = {
  name: 'deployKeyVault'
  params: {
    keyVaultName: keyVaultName
    location: location
  }
}

module logAnalyticsWorkspace './logAnalyticsWorkspace.bicep' = {
  name: 'deployLogAnalyticsWorkspace'
  params: {
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    location: location
  }
}

module apimInstance './apimInstance.bicep' = {
  name: 'deployApimInstance'
  params: {
    apimInstanceName: apimInstanceName
    location: location
  }
}

module redisCache './redisCache.bicep' = {
  name: 'deployRedisCache'
  params: {
    redisCacheName: redisCacheName
    location: location
  }
}

module cosmosAccount './cosmosAccount.bicep' = {
  name: 'deployCosmosAccount'
  params: {
    cosmosAccountName: cosmosAccountName
    location: location
  }
}

module storageAccount './storageAccount.bicep' = {
  name: 'deployStorageAccount'
  params: {
    storageAccountName: storageAccountName
    location: location
  }
}

// Application Insights for observability (OpenTelemetry export target)
module appInsights './appInsights.bicep' = {
  name: 'deployAppInsights'
  dependsOn: [
    logAnalyticsWorkspace
  ]
  params: {
    appInsightsName: appInsightsName
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    location: location
  }
}

module logAnalyticsWorkbook './logAnalyticsWorkbook.bicep' = {
  name: 'deployLogAnalyticsWorkbook'
  dependsOn: [
    appInsights
    logAnalyticsWorkspace
  ]
  params: {
    workbookDisplayName: workbookDisplayName
    appInsightsName: appInsightsName
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    location: location
  }
}

// User-assigned managed identity for ACR image pull — created before the Container
// App so the AcrPull role can be granted first, avoiding the bootstrap race condition
// that occurs with system-assigned identity (which only exists after the app is created).
resource containerAppAcrIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (!empty(acrName)) {
  name: 'id-${containerAppName}'
  location: location
  tags: tags
}

// Azure Container App — single .NET 10 API hosting log ingestion + dashboard
// Replaces both the Python Function App and Python backend App Service
module containerApp './containerApp.bicep' = {
  name: 'deployContainerApp'
  dependsOn: [
    apimInstance
    redisCache
    cosmosAccount
    storageAccount
    roleAssignmentContainerAppAcr
  ]
  params: {
    containerAppName: containerAppName
    containerAppEnvName: containerAppEnvName
    containerImage: containerImage
    location: location
    redisCacheName: redisCacheName
    cosmosEndpoint: cosmosAccount.outputs.cosmosEndpoint
    aiServiceEndpoint: aiServices.outputs.endpoint
    subscriptionId: subscription().subscriptionId
    azureResourceGroup: resourceGroup().name
    appInsightsConnectionString: appInsights.outputs.appInsightsConnectionString
    purviewClientAppId: purviewClientAppId
    entraIdTenantId: entraIdTenantId
    containerAppClientId: containerAppClientId
    acrLoginServer: acrLoginServer
    acrIdentityId: !empty(acrName) ? containerAppAcrIdentity.id : ''
    minReplicas: 1
    maxReplicas: 10
  }
}

module keyVaultAccessPolicy './keyVaultAccessPolicy.bicep' = {
  name: 'assignKeyVaultAccessPolicy'
  params: {
    keyVaultName: keyVaultName
    principalId: containerApp.outputs.containerAppPrincipalId
  }
}

// Azure OpenAI Service deployment
module aiServices './aiService.bicep' = {
  name: resolvedAiServiceName
  params: {
    name: resolvedAiServiceName
    location: location
    tags: union(tags, {})
    deployments: [
      {
        name: 'gpt-4.1'
        model: {
          format: 'OpenAI'
          name: 'gpt-4.1'
          version: '2025-04-14'                   
        }
        raiPolicyName: 'Microsoft.Default'
        versionUpgradeOption: 'OnceCurrentVersionExpired'
        sku: {
          name: 'GlobalStandard'
          capacity: 10
        }        
      }
      {
        name: 'gpt-4.1-mini'
        model: {
          format: 'OpenAI'
          name: 'gpt-4.1-mini' 
          version: '2025-04-14'
        }
        raiPolicyName: 'Microsoft.Default'
        versionUpgradeOption: 'OnceCurrentVersionExpired'
        sku: {
          name: 'GlobalStandard'
          capacity: 5
        }
      }
      {
        name: 'text-embedding'
        model: {
          format: 'OpenAI'
          name: 'text-embedding-3-large' 
          version: '1'
        }
        raiPolicyName: 'Microsoft.Default'
        versionUpgradeOption: 'OnceCurrentVersionExpired'
        sku: {
          name: 'GlobalStandard'
          capacity: 5
        }
      }      
      // {
      //   name: 'dall-e-3'
      //   model: {
      //     format: 'OpenAI'
      //     name: 'dall-e-3'
      //     version: '3.0'                   
      //   }
      //   scaleSettings: {
      //     scaleType: 'Standard'
      //     capacity: 1
      //   }
      
      // }      
    ]
  }
}



module roleAssignmentAppInsights './roleAssignment.bicep' = {
  name: 'assignAppInsightsRoleToContainerApp'
  scope: resourceGroup()
  params: {
    principalId: containerApp.outputs.containerAppPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05') // Monitoring Reader role
  }
}


// Storage Blob Data Contributor:
// This role allows full access to Azure Storage blob containers and data, including read, write, and delete operations.
// Role Definition ID: ba92f5b4-2d11-453d-a403-e96b0029c9fe

// Storage Blob Data Reader (if only read access is required):
// This role allows read-only access to Azure Storage blob containers and data.
// Role Definition ID: 2a2b9908-6ea1-4ae2-8e65-a410df84e7d1
module roleAssignmentContainerAppStorage './roleAssignment.bicep' = {
  name: 'assignStorageRoleToContainerApp'
  scope: resourceGroup()
  dependsOn: [
    storageAccount
  ]  
  params: {
    principalId: containerApp.outputs.containerAppPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1') // Storage Blob Data Reader role
  }
}

// Entra ID data-plane access policy: grants Container App managed identity
// "Data Owner" on Redis (read/write/pub-sub) via Microsoft Entra authentication.
module redisAccessPolicy './redisAccessPolicy.bicep' = {
  name: 'assignRedisAccessPolicyToContainerApp'
  dependsOn: [
    redisCache
  ]
  params: {
    redisCacheName: redisCacheName
    principalId: containerApp.outputs.containerAppPrincipalId
  }
}



module roleAssignmentApim './roleAssignment.bicep' = {
  name: 'assignKeyVaultRoleToApim'
  scope: resourceGroup()
  params: {
    principalId: apimInstance.outputs.clientId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.security.keyVaultSecretsUser) // Key Vault Secrets User
  }
}

module roleAssignmentOpenAi './roleAssignment.bicep' = {
  name: 'assignOpenAiRoleToApim'
  scope: resourceGroup()
  params: {
    principalId: apimInstance.outputs.clientId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.ai.cognitiveServicesOpenAIUser) // Cognitive Services OpenAI User (least privilege)
  }
}


// APIM calls Container App via Entra ID managed identity token — no Azure RBAC role needed.
// Contributor role removed: APIM only needs Cognitive Services OpenAI User + Key Vault Secrets User.

// AcrPull role for the user-assigned managed identity — granted before the Container
// App is created so image pulls succeed on first deployment.
module roleAssignmentContainerAppAcr './roleAssignment.bicep' = if (!empty(acrName)) {
  name: 'assignAcrPullRoleToContainerApp'
  scope: resourceGroup()
  params: {
    principalId: containerAppAcrIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.containers.acrPull) // AcrPull
  }
}



// Note: Cosmos DB data-plane RBAC is configured via az cosmosdb sql role assignment
// in the setup-azure.ps1 script (Phase 6), not via ARM role assignments.


// Construct the OpenAI Service URL
var openAiServiceUrl = 'https://${aiServices.outputs.host}/openai'
module apimOaiApi './apimOaiApi.bicep' = {
  name: 'deployApimOaiApi'
  dependsOn: [
    apimInstance
    roleAssignmentOpenAi
  ]
  params: {
    apimInstanceName: apimInstanceName
    oaiApiName: oaiApiName
    openAiServiceUrl: openAiServiceUrl
    enableJwt: enableJwt
    enableKeys: enableKeys
  }
}

module diagnosticSettings './diagnosticSettings.bicep' = {
  name: 'deployDiagnosticSettings'
  dependsOn: [
    logAnalyticsWorkspace
    containerApp
  ]
  params: {
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    functionAppName: containerAppName
  }
}

module apimFuncApi './apimFuncApi.bicep' = {
  name: 'deployApimFuncApi'
  params: {
    apimInstanceName: apimInstanceName
    funcApiName: funcApiName
    backendFunctionAppServiceUrl: 'https://${containerApp.outputs.containerAppUrl}'
    managedIdentityClientId: apimInstance.outputs.clientId
  }
}

output containerAppUrlInfo string = containerApp.outputs.containerAppUrl
output appInsightsConnectionString string = appInsights.outputs.appInsightsConnectionString
output logAnalyticsWorkbookId string = logAnalyticsWorkbook.outputs.workbookId
output logAnalyticsWorkbookUrl string = logAnalyticsWorkbook.outputs.workbookPortalUrl

output resourceGroupInfo string = resourceGroup().name
output redisInfo object = {
  name: redisCache.outputs.redisCacheName
  hostName: redisCache.outputs.redisHostName
  principalId: redisCache.outputs.redisPrincipalId
}
output cosmosInfo object = {
  name: cosmosAccount.outputs.cosmosAccountName
  endpoint: cosmosAccount.outputs.cosmosEndpoint
  principalId: cosmosAccount.outputs.cosmosPrincipalId
}
