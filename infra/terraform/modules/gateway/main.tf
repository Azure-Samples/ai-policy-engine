# ---------------------------------------------------------------------------
# Gateway module – API Management instance, OpenAI API, policy, and roles
# ---------------------------------------------------------------------------

terraform {
  required_providers {
    azapi = {
      source = "azure/azapi"
    }
    azurerm = {
      source = "hashicorp/azurerm"
    }
  }
}

# =============================================================================
# API Management
# =============================================================================

resource "azurerm_api_management" "this" {
  name                = "${var.name_prefix}-apim"
  location            = var.location
  resource_group_name = var.resource_group_name
  publisher_email     = var.publisher_email
  publisher_name      = var.publisher_name
  sku_name            = var.sku
  tags                = var.tags

  identity {
    type = "SystemAssigned"
  }
}

# =============================================================================
# Named Values
# =============================================================================

resource "azurerm_api_management_named_value" "entra_tenant_id" {
  name                = "EntraTenantId"
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  display_name        = "EntraTenantId"
  value               = var.tenant_id
}

resource "azurerm_api_management_named_value" "expected_audience" {
  name                = "ExpectedAudience"
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  display_name        = "ExpectedAudience"
  value               = "api://${var.gateway_app_id}"
}

resource "azurerm_api_management_named_value" "container_app_url" {
  name                = "ContainerAppUrl"
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  display_name        = "ContainerAppUrl"
  value               = "https://${var.container_app_fqdn}"
}

resource "azurerm_api_management_named_value" "container_app_audience" {
  name                = "ContainerAppAudience"
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  display_name        = "ContainerAppAudience"
  value               = "api://${var.api_app_id}"
}

# =============================================================================
# OpenAI Backend – referenced by policy as "openAiBackend"
# =============================================================================

resource "azurerm_api_management_backend" "openai" {
  name                = "openAiBackend"
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  protocol            = "http"
  url                 = "${var.ai_service_endpoint}openai"
  title               = "OpenAI Backend"
  description         = "Backend for Azure OpenAI APIs"
}

# =============================================================================
# OpenAI API – full passthrough (wildcard operations)
# =============================================================================

resource "azurerm_api_management_api" "openai_jwt_based" {
  count = var.enable_jwt ? 1 : 0

  name                  = "azure-openai-jwt-based-api"
  api_management_name   = azurerm_api_management.this.name
  resource_group_name   = var.resource_group_name
  display_name          = "Azure OpenAI Service API"
  path                  = "jwt/openai"
  protocols             = ["https"]
  subscription_required = false
  service_url           = "${var.ai_service_endpoint}openai"
  revision              = "1"

  dynamic "import" {
    for_each = var.api_spec_url != "" ? [1] : []
    content {
      content_format = "openapi+json-link"
      content_value  = var.api_spec_url
    }
  }
}

# Per-method catch-all operations — covers routes not in the imported spec (e.g. /responses, /assistants).
# StandardV2 doesn't support wildcard (*) method, so each HTTP method gets its own catch-all.
# When a spec is imported, APIM matches spec-defined routes first; these catch the rest.
locals {
  passthrough_methods = toset(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"])
}

resource "azurerm_api_management_api_operation" "openai_jwt_based_passthrough" {
  for_each = var.enable_jwt ? local.passthrough_methods : toset([])

  operation_id        = "passthrough-${lower(each.key)}"
  api_name            = azurerm_api_management_api.openai_jwt_based[0].name
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  display_name        = "Passthrough ${each.key}"
  method              = each.key
  url_template        = "/*"
}

# =============================================================================
# API Policy – Entra JWT validation, precheck, logging
# Deployed via azapi to send policy XML as a JSON-encoded string (same as Bicep),
# avoiding XML entity-escaping issues with C# generics and string literals.
# =============================================================================

resource "azapi_resource" "openai_jwt_based_policy" {
  count = var.enable_jwt ? 1 : 0

  type      = "Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview"
  name      = "policy"
  parent_id = azurerm_api_management_api.openai_jwt_based[0].id

  body = {
    properties = {
      format = "rawxml"
      value  = file(var.jwt_policy_xml_path)
    }
  }

  depends_on = [
    azurerm_api_management_backend.openai,
    azurerm_api_management_named_value.entra_tenant_id,
    azurerm_api_management_named_value.expected_audience,
    azurerm_api_management_named_value.container_app_url,
    azurerm_api_management_named_value.container_app_audience,
  ]
}

# =============================================================================
# OpenAI Key-Based API – subscription-key authenticated passthrough
# =============================================================================

resource "azurerm_api_management_api" "openai_key_based" {
  count = var.enable_keys ? 1 : 0

  name                  = "azure-openai-key-based-api"
  api_management_name   = azurerm_api_management.this.name
  resource_group_name   = var.resource_group_name
  display_name          = "Azure OpenAI Key-Based API"
  path                  = "keys/openai"
  protocols             = ["https"]
  subscription_required = true
  service_url           = "${var.ai_service_endpoint}openai"
  revision              = "1"

  subscription_key_parameter_names {
    header = "api-key"
    query = "api-key"
  }

  dynamic "import" {
    for_each = var.api_spec_url != "" ? [1] : []
    content {
      content_format = "openapi+json-link"
      content_value  = var.api_spec_url
    }
  }
}

resource "azurerm_api_management_api_operation" "openai_key_based_passthrough" {
  for_each = var.enable_keys ? local.passthrough_methods : toset([])

  operation_id        = "key-passthrough-${lower(each.key)}"
  api_name            = azurerm_api_management_api.openai_key_based[0].name
  api_management_name = azurerm_api_management.this.name
  resource_group_name = var.resource_group_name
  display_name        = "Key Passthrough ${each.key}"
  method              = each.key
  url_template        = "/*"
}

resource "azapi_resource" "openai_key_based_policy" {
  count = var.enable_keys ? 1 : 0

  type      = "Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview"
  name      = "policy"
  parent_id = azurerm_api_management_api.openai_key_based[0].id

  body = {
    properties = {
      format = "rawxml"
      value  = file(var.subscription_key_policy_xml_path)
    }
  }

  depends_on = [
    azurerm_api_management_backend.openai,
    azurerm_api_management_named_value.entra_tenant_id,
    azurerm_api_management_named_value.expected_audience,
    azurerm_api_management_named_value.container_app_url,
    azurerm_api_management_named_value.container_app_audience,
  ]
}

# =============================================================================
# Role Assignments
# =============================================================================

# APIM identity → Cognitive Services User on AI Services
resource "azurerm_role_assignment" "apim_cognitive_services_user" {
  scope                = var.ai_service_id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_api_management.this.identity[0].principal_id
}

# APIM identity → Key Vault Secrets User on Key Vault
resource "azurerm_role_assignment" "apim_key_vault_secrets_user" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_api_management.this.identity[0].principal_id
}

# APIM identity → Contributor on Container App
resource "azurerm_role_assignment" "apim_container_app_contributor" {
  scope                = var.container_app_id
  role_definition_name = "Contributor"
  principal_id         = azurerm_api_management.this.identity[0].principal_id
}
