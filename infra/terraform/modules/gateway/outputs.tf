output "apim_name" {
  description = "Name of the API Management instance."
  value       = azurerm_api_management.this.name
}

output "apim_gateway_url" {
  description = "Gateway URL of the API Management instance."
  value       = azurerm_api_management.this.gateway_url
}

output "apim_principal_id" {
  description = "Principal ID of the API Management managed identity."
  value       = azurerm_api_management.this.identity[0].principal_id
}
