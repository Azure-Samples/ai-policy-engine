# =============================================================================
# Root outputs – useful deployment information
# =============================================================================

output "resource_group_name" {
  description = "Name of the resource group."
  value       = azurerm_resource_group.this.name
}

output "container_app_url" {
  description = "HTTPS URL of the Container App."
  value       = "https://${module.compute.container_app_fqdn}"
}

output "apim_gateway_url" {
  description = "Gateway URL of the API Management instance."
  value       = module.gateway.apim_gateway_url
}

output "key_vault_name" {
  description = "Name of the Key Vault instance."
  value       = module.compute.key_vault_name
}

output "api_app_id" {
  description = "Application (client) ID of the AI Policy API app registration."
  value       = module.identity.api_app_id
}

output "gateway_app_id" {
  description = "Application (client) ID of the APIM Gateway app registration."
  value       = module.identity.gateway_app_id
}

output "client1_app_id" {
  description = "Application (client) ID of the AI Policy Sample Client."
  value       = module.identity.client1_app_id
}

output "client1_secret" {
  description = "Client secret for the AI Policy Sample Client."
  value       = module.identity.client1_secret
  sensitive   = true
}

output "client2_app_id" {
  description = "Application (client) ID of the AI Policy Demo Client 2."
  value       = module.identity.client2_app_id
}

output "client2_secret" {
  description = "Client secret for the AI Policy Demo Client 2."
  value       = module.identity.client2_secret
  sensitive   = true
}

output "tenant_id" {
  description = "Azure AD tenant ID."
  value       = module.identity.tenant_id
}

output "secondary_tenant_id" {
  description = "Secondary tenant ID for multi-tenant demo."
  value       = var.secondary_tenant_id
}

output "redis_hostname" {
  description = "Hostname of the Redis Enterprise cluster."
  value       = module.data.redis_hostname
}

output "cosmos_endpoint" {
  description = "Endpoint of the Cosmos DB account."
  value       = module.data.cosmos_endpoint
}

output "app_insights_connection_string" {
  description = "Application Insights connection string."
  value       = module.monitoring.app_insights_connection_string
  sensitive   = true
}

output "AZURE_CONTAINER_REGISTRY_ENDPOINT" {
  description = "Login server of the Container Registry (used by azd for Docker image publishing)."
  value       = module.compute.acr_login_server
}

output "demo_env_file_path" {
  description = "Path to the generated demo/.env.local file."
  value       = local_file.demo_env.filename
}
