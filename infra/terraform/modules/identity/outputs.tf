output "api_app_id" {
  description = "Application (client) ID of the Chargeback API app registration."
  value       = azuread_application.api.client_id
}

output "api_app_object_id" {
  description = "Object ID of the Chargeback API app registration."
  value       = azuread_application.api.object_id
}

output "api_application_id" {
  description = "Terraform resource ID of the Chargeback API app registration."
  value       = azuread_application.api.id
}

output "api_sp_id" {
  description = "Object ID of the Chargeback API service principal."
  value       = azuread_service_principal.api.object_id
}

output "api_scope_id" {
  description = "ID of the access_as_user OAuth2 permission scope."
  value       = random_uuid.oauth2_scope_id.result
}

output "client1_app_id" {
  description = "Application (client) ID of the Chargeback Sample Client."
  value       = azuread_application.client1.client_id
}

output "client1_application_id" {
  description = "Terraform resource ID of the Chargeback Sample Client app registration."
  value       = azuread_application.client1.id
}

output "client1_secret" {
  description = "Client secret for the Chargeback Sample Client."
  value       = azuread_application_password.client1.value
  sensitive   = true
}

output "client2_app_id" {
  description = "Application (client) ID of the Chargeback Demo Client 2."
  value       = azuread_application.client2.client_id
}

output "client2_secret" {
  description = "Client secret for the Chargeback Demo Client 2."
  value       = azuread_application_password.client2.value
  sensitive   = true
}

output "tenant_id" {
  description = "Tenant ID from the current Azure AD client configuration."
  value       = data.azuread_client_config.current.tenant_id
}

output "role_apim_id" {
  description = "UUID of the Chargeback.Apim app role."
  value       = random_uuid.role_apim.result
}

output "gateway_app_id" {
  description = "Application (client) ID of the APIM Gateway app registration."
  value       = azuread_application.gateway.client_id
}

output "gateway_application_id" {
  description = "Terraform resource ID of the APIM Gateway app registration."
  value       = azuread_application.gateway.id
}

output "gateway_scope_id" {
  description = "ID of the access_as_user OAuth2 permission scope on the gateway app."
  value       = random_uuid.gateway_scope_id.result
}
