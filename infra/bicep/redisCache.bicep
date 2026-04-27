param redisCacheName string
param location string

resource redisEnterprise 'Microsoft.Cache/redisEnterprise@2025-04-01' = {
  name: redisCacheName
  location: location
  properties: {
    highAvailability: 'Enabled'
    minimumTlsVersion: '1.2'
  }
  sku: {
    name: 'Balanced_B0'
  }
  identity: {
    type: 'SystemAssigned'
  }
}

resource defaultDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = {
  parent: redisEnterprise
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    clusteringPolicy: 'OSSCluster'
    evictionPolicy: 'VolatileLRU'
    accessKeysAuthentication: 'Disabled'
    port: 10000
  }
}

output redisCacheName string = redisEnterprise.name
output redisHostName string = '${redisEnterprise.name}.${location}.redis.azure.net'
output redisPort int = 10000
output redisPrincipalId string = redisEnterprise.identity.principalId
