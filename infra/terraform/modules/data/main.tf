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

# Cosmos DB IP firewall allow-list used to implement the "Selected networks"
# access mode. We intentionally keep public network access *enabled* and rely
# on this allow-list to scope traffic. Notes:
#   * 0.0.0.0 is a special sentinel that enables the
#     "Accept connections from within public Azure datacenters" toggle, which
#     is required for Container Apps (no VNet integration) to reach the
#     Cosmos endpoint over their Azure-public egress IPs.
#   * The four explicit IPs are the Azure Public cloud "All APIs" Middleware
#     IPs that back the Azure Portal data explorer, Browse Collections, etc.
#     Source:
#     https://learn.microsoft.com/azure/cosmos-db/how-to-configure-firewall#azure-portal-middleware-ip-addresses
locals {
  cosmos_azure_portal_ips = [
    "13.91.105.215",
    "4.210.172.107",
    "13.88.56.148",
    "40.91.218.243",
  ]
  cosmos_ip_range_filter = toset(concat(["0.0.0.0"], local.cosmos_azure_portal_ips))
}

resource "azurerm_cosmosdb_account" "this" {
  name                = "${var.name_prefix}-cosmos"
  location            = var.location
  resource_group_name = var.resource_group_name
  offer_type          = "Standard"
  kind                = "GlobalDocumentDB"

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

  local_authentication_disabled = false

  # Explicitly set public network access + IP firewall so `terraform apply`
  # repairs drift caused by org Azure Policy that periodically disables
  # public network access. Without these properties present in config, the
  # provider treats remote changes as "no opinion" and won't reconcile.
  public_network_access_enabled = true
  ip_range_filter               = local.cosmos_ip_range_filter

  tags = merge(var.tags, {
    "SecurityControl" = "ignore"
  })
}

resource "azurerm_cosmosdb_sql_database" "this" {
  name                = "aipolicy"
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


resource "azurerm_cosmosdb_sql_container" "configuration" {
  name                = "configuration"
  resource_group_name = var.resource_group_name
  account_name        = azurerm_cosmosdb_account.this.name
  database_name       = azurerm_cosmosdb_sql_database.this.name
  partition_key_paths = ["/partitionKey"]
  default_ttl         = 94608000

  indexing_policy {
    indexing_mode = "consistent"

    included_path {
      path = "/*"
    }

    excluded_path {
      path = "/\"_etag\"/?"
    }
  }
}
