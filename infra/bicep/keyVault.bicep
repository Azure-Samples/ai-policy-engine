// for now, this keyvault is not used, but it is created for future use

param keyVaultName string
param location string

resource keyVault 'Microsoft.KeyVault/vaults@2021-04-01-preview' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    accessPolicies: [] // Include an empty accessPolicies array
  }
}

output keyVaultUri string = keyVault.properties.vaultUri
