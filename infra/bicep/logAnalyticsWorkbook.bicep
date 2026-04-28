@description('Display name for the Azure Monitor workbook.')
param workbookDisplayName string = 'AI Policy Log Analytics Dashboard'

@description('Name of the Log Analytics workspace.')
param logAnalyticsWorkspaceName string

@description('Name of the Application Insights component.')
param appInsightsName string

@description('Location for the workbook resource.')
param location string

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = {
  name: logAnalyticsWorkspaceName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

var workbookName = guid(resourceGroup().id, workbookDisplayName, logAnalyticsWorkspace.id)

var workbookData = {
  version: 'Notebook/1.0'
  items: [
    {
      type: 1
      content: {
        json: '## AI Policy Operations Dashboard\nThis workbook pulls 30-day operational AI Policy data from Log Analytics / Application Insights telemetry.'
      }
      name: 'text - 0'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Daily tokens and cost-to-us (30 days)'
        query: '''
union isfuzzy=true AppTraces, traces
| where TimeGenerated >= ago(30d)
| where tostring(column_ifexists("Message", column_ifexists("message", ""))) has "Usage trace exported"
| extend dimensions = todynamic(coalesce(column_ifexists("Properties", dynamic(null)), column_ifexists("CustomDimensions", dynamic(null))))
| extend totalTokens = tolong(dimensions.TotalTokens), costToUs = todouble(dimensions.CostToUs)
| summarize TotalTokens = sum(totalTokens), TotalCostToUs = round(sum(costToUs), 4) by bin(TimeGenerated, 1d)
| order by TimeGenerated asc
'''
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        visualization: 'timechart'
        size: 0
        timeContext: {
          durationMs: 2592000000
        }
      }
      name: 'query - 1'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Top clients by cost (30 days)'
        query: '''
union isfuzzy=true AppTraces, traces
| where TimeGenerated >= ago(30d)
| where tostring(column_ifexists("Message", column_ifexists("message", ""))) has "Usage trace exported"
| extend dimensions = todynamic(coalesce(column_ifexists("Properties", dynamic(null)), column_ifexists("CustomDimensions", dynamic(null))))
| extend clientAppId = tostring(dimensions.ClientAppId), totalTokens = tolong(dimensions.TotalTokens), costToUs = todouble(dimensions.CostToUs)
| summarize Requests = count(), TotalTokens = sum(totalTokens), TotalCostToUs = round(sum(costToUs), 4) by clientAppId
| top 20 by TotalCostToUs desc
'''
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        visualization: 'table'
        size: 0
        timeContext: {
          durationMs: 2592000000
        }
      }
      name: 'query - 2'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Request status breakdown (30 days)'
        query: '''
union isfuzzy=true AppTraces, traces
| where TimeGenerated >= ago(30d)
| where tostring(column_ifexists("Message", column_ifexists("message", ""))) has "Usage trace exported"
| extend dimensions = todynamic(coalesce(column_ifexists("Properties", dynamic(null)), column_ifexists("CustomDimensions", dynamic(null))))
| extend statusCode = toint(dimensions.StatusCode), totalTokens = tolong(dimensions.TotalTokens), costToUs = todouble(dimensions.CostToUs)
| summarize Requests = count(), Tokens = sum(totalTokens), CostToUs = round(sum(costToUs), 4) by statusCode
| order by statusCode asc
'''
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        visualization: 'barchart'
        size: 0
        timeContext: {
          durationMs: 2592000000
        }
      }
      name: 'query - 3'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Quota / rate-limit event timeline (30 days)'
        query: '''
union isfuzzy=true AppTraces, traces
| where TimeGenerated >= ago(30d)
| where tostring(column_ifexists("Message", column_ifexists("message", ""))) has_any ("Rate limit exceeded", "Quota exceeded", "Over quota")
| summarize EventCount = count() by Event = tostring(column_ifexists("Message", column_ifexists("message", ""))), bin(TimeGenerated, 1d)
| order by TimeGenerated asc
'''
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        visualization: 'timechart'
        size: 0
        timeContext: {
          durationMs: 2592000000
        }
      }
      name: 'query - 4'
    }
  ]
  isLocked: false
}

resource workbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: workbookName
  location: location
  kind: 'shared'
  properties: {
    displayName: workbookDisplayName
    serializedData: string(workbookData)
    version: 'Notebook/1.0'
    sourceId: appInsights.id
    category: 'workbook'
  }
}

output workbookId string = workbook.id
output workbookName string = workbook.name
output workbookPortalUrl string = 'https://portal.azure.com/#resource${workbook.id}'
