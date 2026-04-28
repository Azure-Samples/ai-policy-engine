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

variable "subscription_id" {
  description = "Azure subscription ID."
  type        = string
}

variable "log_analytics_workspace_id" {
  description = "Resource ID of the Log Analytics workspace for the Container App Environment."
  type        = string
}

variable "log_analytics_workspace_customer_id" {
  description = "Workspace (customer) ID of the Log Analytics workspace for OTEL config."
  type        = string
}

variable "log_analytics_workspace_shared_key" {
  description = "Primary shared key of the Log Analytics workspace for OTEL config."
  type        = string
  sensitive   = true
}

variable "redis_hostname" {
  description = "Hostname of the Redis Enterprise cluster."
  type        = string
}

variable "redis_port" {
  description = "Port of the Redis Enterprise database."
  type        = number
}

variable "cosmos_endpoint" {
  description = "Endpoint URI of the Cosmos DB account."
  type        = string
}

variable "ai_service_endpoint" {
  description = "Endpoint of the Azure AI Services account."
  type        = string
}

variable "app_insights_connection_string" {
  description = "Application Insights connection string."
  type        = string
}

variable "purview_client_app_id" {
  description = "Client app ID for Purview integration."
  type        = string
  default     = ""
}

variable "entra_tenant_id" {
  description = "Entra ID (Azure AD) tenant ID."
  type        = string
}

variable "api_app_id" {
  description = "Application (client) ID of the AI Policy API app registration."
  type        = string
}

variable "container_image" {
  description = "Container image to deploy to the Container App."
  type        = string
}

variable "acr_login_server" {
  description = "Login server of an external ACR. Uses the module ACR if not provided."
  type        = string
  default     = ""
}

variable "acr_admin_username" {
  description = "Admin username of an external ACR. Uses the module ACR if not provided."
  type        = string
  default     = ""
}

variable "acr_admin_password" {
  description = "Admin password of an external ACR. Uses the module ACR if not provided."
  type        = string
  default     = ""
  sensitive   = true
}

variable "tags" {
  description = "Tags to apply to all resources."
  type        = map(string)
  default     = {}
}
