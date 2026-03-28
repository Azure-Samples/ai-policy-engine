output "log_analytics_workspace_id" {
  description = "ID of the Log Analytics workspace."
  value       = azurerm_log_analytics_workspace.this.id
}

output "log_analytics_workspace_customer_id" {
  description = "Workspace (customer) ID of the Log Analytics workspace."
  value       = azurerm_log_analytics_workspace.this.workspace_id
}

output "log_analytics_workspace_shared_key" {
  description = "Primary shared key of the Log Analytics workspace."
  value       = azurerm_log_analytics_workspace.this.primary_shared_key
  sensitive   = true
}

output "app_insights_connection_string" {
  description = "Application Insights connection string."
  value       = azurerm_application_insights.this.connection_string
  sensitive   = true
}

output "app_insights_instrumentation_key" {
  description = "Application Insights instrumentation key."
  value       = azurerm_application_insights.this.instrumentation_key
  sensitive   = true
}

output "workbook_id" {
  description = "ID of the Azure Monitor workbook."
  value       = azapi_resource.workbook.id
}
