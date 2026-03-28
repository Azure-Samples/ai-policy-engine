param redisCacheName string
param principalId string

resource redisEnterprise 'Microsoft.Cache/redisEnterprise@2025-04-01' existing = {
  name: redisCacheName
}

resource defaultDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' existing = {
  parent: redisEnterprise
  name: 'default'
}

resource accessPolicyAssignment 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01' = {
  name: 'containerappDataOwner'
  parent: defaultDatabase
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: principalId
    }
  }
}
