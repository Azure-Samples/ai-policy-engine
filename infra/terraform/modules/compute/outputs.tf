output "container_app_fqdn" {
  description = "FQDN of the Container App."
  value       = azurerm_container_app.this.ingress[0].fqdn
}

output "container_app_principal_id" {
  description = "Principal ID of the Container App managed identity."
  value       = azurerm_container_app.this.identity[0].principal_id
}

output "container_app_name" {
  description = "Name of the Container App."
  value       = azurerm_container_app.this.name
}

output "acr_login_server" {
  description = "Login server of the Container Registry."
  value       = azurerm_container_registry.this.login_server
}

output "acr_admin_username" {
  description = "Admin username of the Container Registry."
  value       = azurerm_container_registry.this.admin_username
}

output "acr_admin_password" {
  description = "Admin password of the Container Registry."
  value       = azurerm_container_registry.this.admin_password
  sensitive   = true
}

output "storage_account_principal_id" {
  description = "Principal ID of the Storage Account managed identity."
  value       = azurerm_storage_account.this.identity[0].principal_id
}

output "key_vault_uri" {
  description = "URI of the Key Vault."
  value       = azurerm_key_vault.this.vault_uri
}

output "key_vault_id" {
  description = "Resource ID of the Key Vault."
  value       = azurerm_key_vault.this.id
}

output "container_app_id" {
  description = "Resource ID of the Container App."
  value       = azurerm_container_app.this.id
}
