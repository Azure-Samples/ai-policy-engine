param apimInstanceName string
param oaiApiName string
param openAiServiceUrl string


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

resource apimOaiApi 'Microsoft.ApiManagement/service/apis@2021-08-01' = {
  parent: apimInstance
  name: oaiApiName
  properties: {
    displayName: 'Azure OpenAI Service API'
    path: 'openai'
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
resource apimOaiApiPassthrough 'Microsoft.ApiManagement/service/apis/operations@2021-08-01' = [for method in passthroughMethods: {
  parent: apimOaiApi
  name: 'passthrough-${toLower(method)}'
  properties: {
    displayName: 'Passthrough ${method}'
    method: method
    urlTemplate: '/*'
  }
}]

resource apimOaiApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2021-08-01' = {
  parent: apimOaiApi
  name: 'policy'
  dependsOn: [
    openAiBackend
  ]
  properties: {
    format: 'rawxml'
    value: loadTextContent('../../policies/entra-jwt-policy.xml')
  }
}
