output "endpoint" {
  description = "Endpoint of the Azure AI Services account."
  value       = azurerm_cognitive_account.this.endpoint
}

output "host" {
  description = "Hostname parsed from the endpoint URL."
  value       = replace(replace(azurerm_cognitive_account.this.endpoint, "https://", ""), "/", "")
}

output "name" {
  description = "Name of the Azure AI Services account."
  value       = azurerm_cognitive_account.this.name
}

output "principal_id" {
  description = "Principal ID of the Azure AI Services managed identity."
  value       = azurerm_cognitive_account.this.identity[0].principal_id
}

output "id" {
  description = "Resource ID of the Azure AI Services account."
  value       = azurerm_cognitive_account.this.id
}
