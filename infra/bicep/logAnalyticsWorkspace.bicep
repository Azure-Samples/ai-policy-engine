param logAnalyticsWorkspaceName string
param location string

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2021-06-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
      //capacityReservationLevel: 100
    }
  }
}

output logAnalyticsWorkspaceId string = logAnalyticsWorkspace.id
