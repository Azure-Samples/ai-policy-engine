variable "name_prefix" {
  description = "Prefix for resource names."
  type        = string
}

variable "location" {
  description = "Azure region for resources."
  type        = string
}

variable "resource_group_name" {
  description = "Name of the resource group."
  type        = string
}

variable "sku" {
  description = "SKU of the API Management instance."
  type        = string
  default     = "StandardV2_1"
}

variable "publisher_email" {
  description = "Publisher email for the API Management instance."
  type        = string
}

variable "publisher_name" {
  description = "Publisher name for the API Management instance."
  type        = string
}

variable "api_spec_url" {
  description = "URL of the OpenAPI specification for the Azure OpenAI Service API. Empty string enables wildcard passthrough."
  type        = string
  default     = ""
}

variable "ai_service_endpoint" {
  description = "Endpoint of the Azure AI Services account."
  type        = string
}

variable "ai_service_id" {
  description = "Resource ID of the Azure AI Services account for role assignment."
  type        = string
}

variable "container_app_fqdn" {
  description = "FQDN of the Container App."
  type        = string
}

variable "container_app_id" {
  description = "Resource ID of the Container App for role assignment. Empty string to skip."
  type        = string
  default     = ""
}

variable "api_app_id" {
  description = "Application (client) ID of the Chargeback API app registration."
  type        = string
}

variable "gateway_app_id" {
  description = "Application (client) ID of the APIM Gateway app registration (multi-tenant, client-facing)."
  type        = string
}

variable "tenant_id" {
  description = "Entra ID (Azure AD) tenant ID."
  type        = string
}

variable "key_vault_id" {
  description = "Resource ID of the Key Vault for role assignment. Empty string to skip."
  type        = string
  default     = ""
}

variable "enable_jwt" {
  description = "Enable the JWT-authenticated OpenAI API endpoint."
  type        = bool
  default     = true
}

variable "enable_keys" {
  description = "Enable the subscription-key-authenticated OpenAI API endpoint."
  type        = bool
  default     = true
}

variable "jwt_policy_xml_path" {
  description = "Path to the Entra JWT APIM policy XML file (entra-jwt-policy.xml)."
  type        = string
  default     = ""
}

variable "subscription_key_policy_xml_path" {
  description = "Path to the subscription-key APIM policy XML file (subscription-key-policy.xml)."
  type        = string
  default     = ""
}

variable "tags" {
  description = "Tags to apply to all resources."
  type        = map(string)
  default     = {}
}
