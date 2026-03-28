terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.65"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.8"
    }
    azapi = {
      source  = "azure/azapi"
      version = "~> 2.8"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy = true
    }
    cognitive_account {
      purge_soft_delete_on_destroy = false
    }
  }
  subscription_id      = var.subscription_id
  storage_use_azuread  = true
}

provider "azuread" {}
provider "azapi" {}
