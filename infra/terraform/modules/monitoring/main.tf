terraform {
  required_providers {
    azurerm = {
      source = "hashicorp/azurerm"
    }
    azapi = {
      source = "azure/azapi"
    }
  }
}

resource "azurerm_log_analytics_workspace" "this" {
  name                = "${var.name_prefix}-law"
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "PerGB2018"
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "${var.name_prefix}-ai"
  location            = var.location
  resource_group_name = var.resource_group_name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  tags                = var.tags
}

locals {
  workbook_content = jsonencode({
    version = "Notebook/1.0"
    items = [
      {
        type = 3
        content = {
          version = "KqlItem/1.0"
          query   = "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(30d) | summarize TotalTokens=sum(toint(totalTokens_s)), EstimatedCost=sum(todouble(estimatedCost_s)) by bin(TimeGenerated, 1d) | order by TimeGenerated asc"
          size    = 0
          title   = "Daily Tokens and Cost (30 days)"
          timeContext = {
            durationMs = 2592000000
          }
          queryType    = 0
          resourceType = "microsoft.operationalinsights/workspaces"
        }
        name = "daily-tokens-cost"
      },
      {
        type = 3
        content = {
          version = "KqlItem/1.0"
          query   = "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(30d) | summarize TotalCost=sum(todouble(estimatedCost_s)) by clientAppId_s | top 10 by TotalCost desc"
          size    = 0
          title   = "Top Clients by Cost (30 days)"
          timeContext = {
            durationMs = 2592000000
          }
          queryType    = 0
          resourceType = "microsoft.operationalinsights/workspaces"
        }
        name = "top-clients-cost"
      },
      {
        type = 3
        content = {
          version = "KqlItem/1.0"
          query   = "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(30d) | summarize Count=count() by statusCode_s | order by Count desc"
          size    = 0
          title   = "Request Status Breakdown (30 days)"
          timeContext = {
            durationMs = 2592000000
          }
          queryType    = 0
          resourceType = "microsoft.operationalinsights/workspaces"
        }
        name = "request-status-breakdown"
      },
      {
        type = 3
        content = {
          version = "KqlItem/1.0"
          query   = "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(30d) | where statusCode_s == '429' or Log_s contains 'rate limit' or Log_s contains 'quota' | summarize Count=count() by bin(TimeGenerated, 1d) | order by TimeGenerated asc"
          size    = 0
          title   = "Quota/Rate-Limit Events (30 days)"
          timeContext = {
            durationMs = 2592000000
          }
          queryType    = 0
          resourceType = "microsoft.operationalinsights/workspaces"
        }
        name = "quota-rate-limit-events"
      }
    ]
    isLocked = false
  })
}

resource "azapi_resource" "workbook" {
  type      = "Microsoft.Insights/workbooks@2022-04-01"
  name      = uuidv5("url", "${var.name_prefix}-aipolicy-workbook")
  location  = var.location
  parent_id = "/subscriptions/${data.azurerm_subscription.current.subscription_id}/resourceGroups/${var.resource_group_name}"
  tags      = var.tags

  body = {
    kind = "shared"
    properties = {
      displayName    = "${var.name_prefix} AI Policy Dashboard"
      serializedData = local.workbook_content
      category       = "workbook"
      sourceId       = azurerm_application_insights.this.id
    }
  }
}

data "azurerm_subscription" "current" {}
