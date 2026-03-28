
param name string
param location string = resourceGroup().location
param tags object = {}
param deployments array = []
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

resource aiServices 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  properties: {
    customSubDomainName: toLower(name)
    publicNetworkAccess: publicNetworkAccess
  }
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
}

@batchSize(1)
resource openAiDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = [
  for deployment in deployments: {
    parent: aiServices
    name: deployment.name
    properties: {
      model: deployment.?model ?? null
      raiPolicyName: deployment.?raiPolicyName ?? null
      versionUpgradeOption: deployment.?versionUpgradeOption ?? 'OnceCurrentVersionExpired'
    }
    sku: deployment.?sku ?? {
      name: 'Standard'
      capacity: 20
    }
}]

@description('ID for the deployed Azure OpenAI Service.')
output id string = aiServices.id

@description('Name for the deployed Azure OpenAI Service.')
output name string = aiServices.name

@description('Endpoint for the deployed Azure OpenAI Service.')
output endpoint string = aiServices.properties.endpoint

@description('Host for the deployed Azure OpenAI Service.')
output host string = split(aiServices.properties.endpoint, '/')[2]

@description('Principal ID for the deployed Azure OpenAI Service.')
output principalId string = aiServices.identity.principalId
