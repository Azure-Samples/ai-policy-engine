@description('Name of the Cosmos DB account')
param cosmosAccountName string

@description('Location for the Cosmos DB account')
param location string

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    disableLocalAuth: true
    databaseAccountOfferType: 'Standard'
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
  tags: {
    SecurityControl: 'ignore'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'aipolicy'
  properties: {
    resource: {
      id: 'aipolicy'
    }
  }
}

resource auditLogsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'audit-logs'
  properties: {
    resource: {
      id: 'audit-logs'
      partitionKey: {
        paths: ['/customerKey']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/billingPeriod/?' }
          { path: '/customerKey/?' }
          { path: '/clientAppId/?' }
          { path: '/tenantId/?' }
          { path: '/timestamp/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
      defaultTtl: 94608000 // 36 months in seconds (1095 days)
    }
  }
}

resource billingSummariesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'billing-summaries'
  properties: {
    resource: {
      id: 'billing-summaries'
      partitionKey: {
        paths: ['/customerKey']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/billingPeriod/?' }
          { path: '/customerKey/?' }
          { path: '/clientAppId/?' }
          { path: '/tenantId/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
      defaultTtl: 94608000 // 36 months in seconds
    }
  }
}

resource configurationContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'configuration'
  properties: {
    resource: {
      id: 'configuration'
      partitionKey: {
        paths: ['/partitionKey']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/"_etag"/?' }
        ]
      }
      defaultTtl: 94608000 // 36 months in seconds
    }
  }
}

output cosmosAccountName string = cosmosAccount.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output cosmosPrincipalId string = cosmosAccount.identity.principalId
