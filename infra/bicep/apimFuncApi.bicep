param apimInstanceName string
param funcApiName string
param backendFunctionAppServiceUrl string // Backend URL for the Azure Function App
param managedIdentityClientId string // Client ID of the Managed Identity

resource apimInstance 'Microsoft.ApiManagement/service@2021-08-01' existing = {
  name: apimInstanceName
}

resource apimBackend 'Microsoft.ApiManagement/service/backends@2021-08-01' = {
  parent: apimInstance
  name: 'functionBackend'
  properties: {
    url: backendFunctionAppServiceUrl // Backend URL for the Azure Function App
    protocol: 'http'
    title: 'Function Backend'
    description: 'Backend for Azure Function App'
    credentials: {
      authorization: {
        scheme: 'ManagedIdentity'
        parameter: managedIdentityClientId
      }
    }
  }
}

resource apimFuncApi 'Microsoft.ApiManagement/service/apis@2021-08-01' = {
  parent: apimInstance
  name: funcApiName
  properties: {
    displayName: funcApiName
    serviceUrl: backendFunctionAppServiceUrl // Backend URL for the Azure Function App
    path: funcApiName
    protocols: [
      'http','https'
    ]
    subscriptionRequired: false // Disable subscription requirement for this API
  }
}

resource getOperation 'Microsoft.ApiManagement/service/apis/operations@2021-08-01' = {
  parent: apimFuncApi
  name: 'get'
  properties: {
    displayName: 'Get'
    method: 'GET'
    urlTemplate: '/'
    request: {
      description: 'Request to get data'
    }
    responses: [
      {
        statusCode: 200
        description: 'Successful response'
      }
    ]
  }
}

resource postOperation 'Microsoft.ApiManagement/service/apis/operations@2021-08-01' = {
  parent: apimFuncApi
  name: 'post'
  properties: {
    displayName: 'Post'
    method: 'POST'
    urlTemplate: '/log' // Ensure this matches the route in function.json
    request: {
      description: 'Request to log data'      
      headers: [
        {
          name: 'Authorization'
          required: false
          type: 'string'
          description: 'Bearer token for authorization'
        }
        {
          name: 'x-api-key'
          required: false
          type: 'string'
          description: 'API key for authorization'
        }
        {
          name: 'Content-Type'
          required: false
          type: 'string'
          defaultValue: 'application/json'
          description: 'The content type of the request body'
        }        
      ]
      // representations: [
      //   {
      //     contentType: 'application/json'
      //     schemaId: 'your-schema-id'
      //   }
      // ]
    }
    responses: [
      {
        statusCode: 200
        description: 'Successful response'
      }
    ]
  }
}

