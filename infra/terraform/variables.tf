variable "location" {
  description = "Azure region for all resources"
  type        = string
  default     = "eastus2"
}

variable "workload_name" {
  description = "Short name used as prefix for all resources"
  type        = string
  default     = "aipolicy"
}

variable "subscription_id" {
  description = "Azure subscription ID"
  type        = string

}

variable "container_image" {
  description = "Container image for the AI Policy API"
  type        = string
  default     = "mcr.microsoft.com/dotnet/aspnet:10.0"
}

variable "secondary_tenant_id" {
  description = "Optional secondary tenant ID for multi-tenant demo. Leave empty to skip."
  type        = string
  default     = ""
}

variable "apim_sku" {
  description = "APIM SKU name"
  type        = string
  default     = "StandardV2_1"
}

variable "apim_publisher_email" {
  description = "APIM publisher email"
  type        = string
  default     = "admin@contoso.com"
}

variable "apim_publisher_name" {
  description = "APIM publisher name"
  type        = string
  default     = "AI Policy Admin"
}

variable "openai_api_spec_url" {
  description = "URL to the OpenAI API spec for APIM import. Set to empty string to use passthrough operations only."
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

variable "purview_client_app_id" {
  description = "Purview client app ID for Agent 365 integration (optional)"
  type        = string
  default     = ""
}

variable "ai_deployments" {
  description = "Azure AI model deployments"
  type = list(object({
    name                   = string
    model_name             = string
    model_format           = string
    model_version          = string
    sku_name               = string
    sku_capacity           = number
    rai_policy_name        = optional(string, "Microsoft.Default")
    version_upgrade_option = optional(string, "OnceCurrentVersionExpired")
  }))
  default = [
    {
      name          = "gpt-4.1"
      model_name    = "gpt-4.1"
      model_format  = "OpenAI"
      model_version = "2025-04-14"
      sku_name      = "GlobalStandard"
      sku_capacity  = 10
    },
    {
      name          = "gpt-4.1-mini"
      model_name    = "gpt-4.1-mini"
      model_format  = "OpenAI"
      model_version = "2025-04-14"
      sku_name      = "GlobalStandard"
      sku_capacity  = 5
    },
    {
      name          = "text-embedding"
      model_name    = "text-embedding-3-large"
      model_format  = "OpenAI"
      model_version = "1"
      sku_name      = "GlobalStandard"
      sku_capacity  = 5
    }
  ]
}
