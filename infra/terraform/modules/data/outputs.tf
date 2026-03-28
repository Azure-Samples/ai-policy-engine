output "redis_hostname" {
  description = "Hostname of the Redis Enterprise cluster."
  value       = "${azapi_resource.redis_cluster.name}.${var.location}.redis.azure.net"
}

output "redis_port" {
  description = "Port of the Redis Enterprise database."
  value       = 10000
}

output "redis_principal_id" {
  description = "Principal ID of the Redis Enterprise cluster managed identity."
  value       = azapi_resource.redis_cluster.identity[0].principal_id
}

output "cosmos_endpoint" {
  description = "Endpoint of the Cosmos DB account."
  value       = azurerm_cosmosdb_account.this.endpoint
}

output "cosmos_account_name" {
  description = "Name of the Cosmos DB account."
  value       = azurerm_cosmosdb_account.this.name
}

output "cosmos_principal_id" {
  description = "Principal ID of the Cosmos DB account managed identity."
  value       = azurerm_cosmosdb_account.this.identity[0].principal_id
}

output "redis_database_id" {
  description = "Resource ID of the Redis Enterprise database."
  value       = azapi_resource.redis_database.id
}
