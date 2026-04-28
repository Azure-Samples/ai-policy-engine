# Azure Bicep Deployment

This repository contains Bicep files for deploying an Azure API Management instance, Container App, Redis Cache, Key Vault, Log Analytics Workspace, Application Insights, and related resources.

## Files

- `main.bicep`: The main Bicep file that orchestrates the deployment of all resources.
- `apimInstance.bicep`: Bicep file for deploying the API Management instance.
- `apimOaiApi.bicep`: Bicep file for deploying the OpenAI API in API Management.
- `apimFuncApi.bicep`: Bicep file for deploying the AI Policy API in API Management.
- `apimLogger.bicep`: Bicep file for configuring the API Management logger.
- `containerApp.bicep`: Bicep file for deploying the Container App (AI Policy API).
- `appInsights.bicep`: Bicep file for deploying Application Insights.
- `backendFrontendApp.bicep`: Bicep file for deploying the backend/frontend Entra app registrations.
- `keyVault.bicep`: Bicep file for deploying the Key Vault.
- `keyVaultAccessPolicy.bicep`: Bicep file for assigning access policies to the Key Vault.
- `logAnalyticsWorkspace.bicep`: Bicep file for deploying the Log Analytics Workspace.
- `redisCache.bicep`: Bicep file for deploying the Redis Cache.
- `storageAccount.bicep`: Bicep file for deploying the Storage Account.
- `roleAssignment.bicep`: Bicep file for assigning roles to the Managed Identity.
- `diagnosticSettings.bicep`: Bicep file for configuring diagnostic settings for the Container App.
- `parameter.json`: Parameters file for providing values to the `main.bicep` file.

## Prerequisites

- Azure CLI: [Install Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
- Azure subscription: [Create an Azure account](https://azure.microsoft.com/en-us/free/)


## Deployment

1. Clone the repository:

    ```sh
    git clone <repository-url>
    cd <repository-directory>
    ```

2. Update the `parameter.json` file with the appropriate values:

    ```json
    {
      "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
      "contentVersion": "1.0.0.0",
      "parameters": {
        "location": {
          "value": "West US"
        },
        "apimInstanceName": {
          "value": "myApiManagementInstance"
        },
        "oaiApiName": {
          "value": "myOaiApi"
        },
        "funcApiName": {
          "value": "myFuncApi"
        },
        "apiSpecFileUri": {
          "value": "https://example.com/apiSpec"
        },
        "containerAppName": {
          "value": "myContainerApp"
        },
        "keyVaultName": {
          "value": "myKeyVault"
        },
        "logAnalyticsWorkspaceName": {
          "value": "myLogAnalyticsWorkspace"
        },
        "storageAccountName": {
          "value": "mystorageaccount"
        },
        "redisCacheName": {
          "value": "myRedisCache"
        },
        "openApiServiceUrl": {
          "value": "https://example.com/openai"
        },
        "containerAppServiceUrl": {
          "value": "https://example.com/containerapp"
        },
        "managedIdentityClientId": {
          "value": "myManagedIdentityClientId"
        }
      }
    }
    ```

3. Deploy the Bicep files using the Azure CLI:

    ```sh
    az deployment group create --resource-group <your-resource-group> --template-file main.bicep --parameters @parameter.json
    ```

    Replace `<your-resource-group>` with the name of your Azure resource group.

## Resources Deployed

- **API Management Instance**: An instance of Azure API Management.
- **OpenAI API**: An API imported from a given JSON file.
- **AI Policy API**: An API for the AI Policy Container App.
- **Container App**: An Azure Container App running the AI Policy API.
- **Application Insights**: Application Insights for telemetry and monitoring.
- **Key Vault**: An Azure Key Vault.
- **Log Analytics Workspace**: A Log Analytics Workspace.
- **Redis Cache**: A Redis Cache.
- **Storage Account**: An Azure Storage Account.
- **Role Assignments**: Role assignments for the managed identity.
- **Diagnostic Settings**: Diagnostic settings for the Container App.

## Notes

- Ensure that the JSON file for the API specification is accessible from the provided URI.
- Customize the `parameter.json` file as needed for your deployment.

## Cleanup

To delete the resources created by this deployment, you can delete the resource group:

```sh
az group delete --name <your-resource-group> --yes --no-wait