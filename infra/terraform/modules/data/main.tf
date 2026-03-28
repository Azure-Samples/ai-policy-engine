terraform {
  required_providers {
    azurerm = {
      source = "hashicorp/azurerm"
    }
    azapi = {
      source = "azure/azapi"
    }
  }
}

# ---------- Redis Enterprise (Managed Redis via azapi) ----------

resource "azapi_resource" "redis_cluster" {
  type      = "Microsoft.Cache/redisEnterprise@2024-10-01"
  name      = "${var.name_prefix}-redis"
  location  = var.location
  parent_id = "/subscriptions/${data.azurerm_subscription.current.subscription_id}/resourceGroups/${var.resource_group_name}"
  tags      = var.tags

  identity {
    type = "SystemAssigned"
  }

  body = {
    sku = {
      name = "Balanced_B0"
    }
    properties = {
      minimumTlsVersion = "1.2"
    }
  }

  response_export_values = ["properties.hostName", "identity.principalId"]
}

resource "azapi_resource" "redis_database" {
  type      = "Microsoft.Cache/redisEnterprise/databases@2024-10-01"
  name      = "default"
  parent_id = azapi_resource.redis_cluster.id

  body = {
    properties = {
      clientProtocol   = "Encrypted"
      clusteringPolicy = "OSSCluster"
      evictionPolicy   = "VolatileLRU"
      port             = 10000
    }
  }
}

resource "azapi_resource" "redis_access_policy" {
  count     = var.container_app_principal_id != "" ? 1 : 0
  type      = "Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01"
  name      = "containerappDataOwner"
  parent_id = azapi_resource.redis_database.id

  body = {
    properties = {
      accessPolicyName = "default"
      user = {
        objectId = var.container_app_principal_id
      }
    }
  }
}

data "azurerm_subscription" "current" {}

# ---------- Cosmos DB ----------

resource "azurerm_cosmosdb_account" "this" {
  name                = "${var.name_prefix}-cosmos"
  location            = var.location
  resource_group_name = var.resource_group_name
  offer_type          = "Standard"
  kind                = "GlobalDocumentDB"
  tags                = var.tags

  capabilities {
    name = "EnableServerless"
  }

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = var.location
    failover_priority = 0
  }

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_cosmosdb_sql_database" "this" {
  name                = "chargeback"
  resource_group_name = var.resource_group_name
  account_name        = azurerm_cosmosdb_account.this.name
}

resource "azurerm_cosmosdb_sql_container" "audit_logs" {
  name                = "audit-logs"
  resource_group_name = var.resource_group_name
  account_name        = azurerm_cosmosdb_account.this.name
  database_name       = azurerm_cosmosdb_sql_database.this.name
  partition_key_paths = ["/customerKey"]
  default_ttl         = 94608000

  indexing_policy {
    indexing_mode = "consistent"

    included_path {
      path = "/billingPeriod/?"
    }
    included_path {
      path = "/customerKey/?"
    }
    included_path {
      path = "/clientAppId/?"
    }
    included_path {
      path = "/tenantId/?"
    }
    included_path {
      path = "/timestamp/?"
    }

    excluded_path {
      path = "/*"
    }
  }
}

resource "azurerm_cosmosdb_sql_container" "billing_summaries" {
  name                = "billing-summaries"
  resource_group_name = var.resource_group_name
  account_name        = azurerm_cosmosdb_account.this.name
  database_name       = azurerm_cosmosdb_sql_database.this.name
  partition_key_paths = ["/customerKey"]
  default_ttl         = 94608000

  indexing_policy {
    indexing_mode = "consistent"

    included_path {
      path = "/billingPeriod/?"
    }
    included_path {
      path = "/customerKey/?"
    }
    included_path {
      path = "/clientAppId/?"
    }
    included_path {
      path = "/tenantId/?"
    }

    excluded_path {
      path = "/*"
    }
  }
}
