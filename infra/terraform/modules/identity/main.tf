# ---------------------------------------------------------------------------
# Identity module – Entra ID app registrations for the chargeback system
# ---------------------------------------------------------------------------

data "azuread_client_config" "current" {}

# Random UUIDs for OAuth2 permission scopes
resource "random_uuid" "oauth2_scope_id" {}
resource "random_uuid" "gateway_scope_id" {}

# Placeholder UUIDs for identifier_uris (replaced by local-exec after creation
# because the real value is api://{client_id} which isn't known until apply time)
resource "random_uuid" "gateway_app_placeholder" {}
resource "random_uuid" "api_app_placeholder" {}

# Random UUIDs for app role IDs
resource "random_uuid" "role_export" {}
resource "random_uuid" "role_admin" {}
resource "random_uuid" "role_apim" {}

# =============================================================================
# APIM Gateway App Registration (multi-tenant)
# External clients authenticate against this app to access OpenAI via APIM.
# =============================================================================

resource "azuread_application" "gateway" {
  display_name     = "Chargeback APIM Gateway"
  sign_in_audience = "AzureADMultipleOrgs"
  identifier_uris  = ["api://${random_uuid.gateway_app_placeholder.result}"]

  api {
    oauth2_permission_scope {
      id                         = random_uuid.gateway_scope_id.result
      admin_consent_display_name = "Access OpenAI via APIM Gateway"
      admin_consent_description  = "Allows the app to call Azure OpenAI endpoints through the APIM chargeback gateway"
      type                       = "Admin"
      value                      = "access_as_user"
      enabled                    = true
    }
  }

  # Microsoft Graph openid — required for third-party tenant consent/install
  required_resource_access {
    resource_app_id = "00000003-0000-0000-c000-000000000000" # Microsoft Graph

    resource_access {
      id   = "37f7f235-527c-4136-accd-4a02d197296e" # openid
      type = "Scope"
    }
  }

  lifecycle {
    ignore_changes = [identifier_uris]
  }
}

# Set the real identifier URI (api://{client_id}) after the app is created.
# The inline identifier_uris uses a placeholder because client_id isn't known
# until after creation. This provisioner updates it to the canonical value.
resource "terraform_data" "gateway_identifier_uri" {
  depends_on = [azuread_application.gateway]

  provisioner "local-exec" {
    command = "az ad app update --id ${azuread_application.gateway.client_id} --identifier-uris api://${azuread_application.gateway.client_id}"
  }
}

resource "azuread_service_principal" "gateway" {
  client_id  = azuread_application.gateway.client_id
  depends_on = [terraform_data.gateway_identifier_uri]
}

# =============================================================================
# Chargeback API App Registration (single-tenant)
# Only APIM's managed identity and dashboard users access this directly.
# =============================================================================

resource "azuread_application" "api" {
  display_name     = "Chargeback API"
  sign_in_audience = "AzureADMyOrg"
  identifier_uris  = ["api://${random_uuid.api_app_placeholder.result}"]

  api {
    oauth2_permission_scope {
      id                         = random_uuid.oauth2_scope_id.result
      admin_consent_display_name = "Access Chargeback API"
      admin_consent_description  = "Allows the app to access the Chargeback API"
      type                       = "Admin"
      value                      = "access_as_user"
      enabled                    = true
    }
  }

  app_role {
    id                   = random_uuid.role_export.result
    display_name         = "Chargeback Export"
    description          = "Can export chargeback data"
    value                = "Chargeback.Export"
    allowed_member_types = ["Application", "User"]
    enabled              = true
  }

  app_role {
    id                   = random_uuid.role_admin.result
    display_name         = "Chargeback Admin"
    description          = "Full administrative access to the chargeback system"
    value                = "Chargeback.Admin"
    allowed_member_types = ["Application", "User"]
    enabled              = true
  }

  app_role {
    id                   = random_uuid.role_apim.result
    display_name         = "APIM Service"
    description          = "API Management service access"
    value                = "Chargeback.Apim"
    allowed_member_types = ["Application"]
    enabled              = true
  }

  required_resource_access {
    resource_app_id = "00000003-0000-0000-c000-000000000000" # Microsoft Graph

    resource_access {
      id   = "37f7f235-527c-4136-accd-4a02d197296e" # openid
      type = "Scope"
    }
  }

  lifecycle {
    ignore_changes = [identifier_uris]
  }
}

# Set the real identifier URI after creation (same pattern as gateway app).
resource "terraform_data" "api_identifier_uri" {
  depends_on = [azuread_application.api]

  provisioner "local-exec" {
    command = "az ad app update --id ${azuread_application.api.client_id} --identifier-uris api://${azuread_application.api.client_id}"
  }
}

resource "azuread_service_principal" "api" {
  client_id  = azuread_application.api.client_id
  depends_on = [terraform_data.api_identifier_uri]
}

# =============================================================================
# Client App 1 – single-tenant sample client → targets APIM Gateway
# =============================================================================

resource "azuread_application" "client1" {
  display_name     = "Chargeback Sample Client"
  sign_in_audience = "AzureADMyOrg"

  required_resource_access {
    resource_app_id = azuread_application.gateway.client_id

    resource_access {
      id   = random_uuid.gateway_scope_id.result
      type = "Scope"
    }
  }

  required_resource_access {
    resource_app_id = "00000003-0000-0000-c000-000000000000" # Microsoft Graph

    resource_access {
      id   = "37f7f235-527c-4136-accd-4a02d197296e" # openid
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "client1" {
  client_id = azuread_application.client1.client_id
}

resource "azuread_application_password" "client1" {
  application_id    = azuread_application.client1.id
  display_name      = "terraform"
  end_date          = timeadd(timestamp(), "8760h")

  lifecycle {
    ignore_changes = [end_date]
  }
}

resource "azuread_app_role_assignment" "client1_admin" {
  app_role_id         = random_uuid.role_admin.result
  principal_object_id = azuread_service_principal.client1.object_id
  resource_object_id  = azuread_service_principal.api.object_id
}

# =============================================================================
# Client App 2 – multi-tenant demo client → targets APIM Gateway
# =============================================================================

resource "azuread_application" "client2" {
  display_name     = "Chargeback Demo Client 2"
  sign_in_audience = "AzureADMultipleOrgs"

  required_resource_access {
    resource_app_id = azuread_application.gateway.client_id

    resource_access {
      id   = random_uuid.gateway_scope_id.result
      type = "Scope"
    }
  }

  required_resource_access {
    resource_app_id = "00000003-0000-0000-c000-000000000000" # Microsoft Graph

    resource_access {
      id   = "37f7f235-527c-4136-accd-4a02d197296e" # openid
      type = "Scope"
    }
  }

  public_client {
    redirect_uris = ["http://localhost:29783"]
  }
}

resource "azuread_service_principal" "client2" {
  client_id = azuread_application.client2.client_id
}

resource "azuread_application_password" "client2" {
  application_id    = azuread_application.client2.id
  display_name      = "terraform"
  end_date          = timeadd(timestamp(), "8760h")

  lifecycle {
    ignore_changes = [end_date]
  }
}

# =============================================================================
# Admin consent grants – delegated permission grants on the GATEWAY app
# =============================================================================

resource "azuread_service_principal_delegated_permission_grant" "client1" {
  service_principal_object_id          = azuread_service_principal.client1.object_id
  resource_service_principal_object_id = azuread_service_principal.gateway.object_id
  claim_values                         = ["access_as_user"]
}

resource "azuread_service_principal_delegated_permission_grant" "client2" {
  service_principal_object_id          = azuread_service_principal.client2.object_id
  resource_service_principal_object_id = azuread_service_principal.gateway.object_id
  claim_values                         = ["access_as_user"]
}
