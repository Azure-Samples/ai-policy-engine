var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

var cosmosDb = cosmos.AddDatabase("chargeback");

var api = builder.AddProject<Projects.Chargeback_Api>("chargeback-api")
    .WithReference(redis)
    .WaitFor(redis)
    .WithReference(cosmosDb)
    .WaitFor(cosmos)
    .WithExternalHttpEndpoints();

builder.Build().Run();
