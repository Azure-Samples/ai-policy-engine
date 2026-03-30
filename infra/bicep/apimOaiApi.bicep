param apimInstanceName string
param oaiApiName string
param openAiServiceUrl string

@description('Deploy the JWT-authenticated OpenAI API endpoint.')
param enableJwt bool = true

@description('Deploy the subscription-key-authenticated OpenAI API endpoint.')
param enableKeys bool = true


resource apimInstance 'Microsoft.ApiManagement/service@2021-08-01' existing = {
  name: apimInstanceName
}

resource openAiBackend 'Microsoft.ApiManagement/service/backends@2021-08-01' = {
  parent: apimInstance
  name: 'openAiBackend'
  properties: {
    url: openAiServiceUrl
    protocol: 'http'
    title: 'OpenAI Backend'
    description: 'Backend for Azure OpenAI APIs'
  }
}

resource apimJwtOaiApi 'Microsoft.ApiManagement/service/apis@2021-08-01' = if (enableJwt) {
  parent: apimInstance
  name: '${oaiApiName}-jwt'
  properties: {
    displayName: 'Azure OpenAI Service API'
    path: 'jwt/openai'
    serviceUrl: openAiServiceUrl
    protocols: [
      'https'
    ]
    subscriptionRequired: false
  }
}

// Per-method catch-all operations — StandardV2 doesn't support wildcard (*) method.
var passthroughMethods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS']

@batchSize(1)
resource apimJwtOaiApiPassthrough 'Microsoft.ApiManagement/service/apis/operations@2021-08-01' = [for method in enableJwt ? passthroughMethods : []: {
  parent: apimJwtOaiApi
  name: 'passthrough-${toLower(method)}'
  properties: {
    displayName: 'Passthrough ${method}'
    method: method
    urlTemplate: '/*'
  }
}]

// Key-based API – subscription-key authenticated passthrough
resource apimKeyOaiApi 'Microsoft.ApiManagement/service/apis@2021-08-01' = if (enableKeys) {
  parent: apimInstance
  name: '${oaiApiName}-keys'
  properties: {
    displayName: 'Azure OpenAI Key-Based API'
    path: 'keys/openai'
    serviceUrl: openAiServiceUrl
    protocols: [
      'https'
    ]
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'api-key'
    }
  }
}

@batchSize(1)
resource apimKeyOaiApiPassthrough 'Microsoft.ApiManagement/service/apis/operations@2021-08-01' = [for method in enableKeys ? passthroughMethods : []: {
  parent: apimKeyOaiApi
  name: 'key-passthrough-${toLower(method)}'
  properties: {
    displayName: 'Key Passthrough ${method}'
    method: method
    urlTemplate: '/*'
  }
}]

