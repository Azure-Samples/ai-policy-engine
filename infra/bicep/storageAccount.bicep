@description('Name of the Storage Account')
param storageAccountName string

@description('Location for the Storage Account')
param location string

resource storageAccount 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
  }
}

// // Output the Storage Account connection string
// output connectionString string = storageAccount.listKeys().keys[0].value

output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name

// Output the Storage Account Principal ID for role assignments
output principalId string = storageAccount.identity.principalId
