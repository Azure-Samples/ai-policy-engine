param logAnalyticsWorkspaceName string
param functionAppName string // Container App name (legacy param name kept for compatibility)

resource containerApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: functionAppName
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2021-06-01' existing = {
  name: logAnalyticsWorkspaceName
  scope: resourceGroup()
}

resource diagnosticSettingsContainerApp 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: '${functionAppName}-${logAnalyticsWorkspaceName}'
  scope: containerApp
  properties: {
    workspaceId: logAnalyticsWorkspace.id
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
