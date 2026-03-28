variable "name" {
  description = "Name of the Azure AI Services account."
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

variable "deployments" {
  description = "List of model deployments to create."
  type = list(object({
    name                   = string
    model_name             = string
    model_format           = string
    model_version          = string
    sku_name               = string
    sku_capacity           = number
    rai_policy_name        = optional(string, null)
    version_upgrade_option = optional(string, "OnceNewDefaultVersionAvailable")
  }))
  default = []
}

variable "tags" {
  description = "Tags to apply to all resources."
  type        = map(string)
  default     = {}
}
