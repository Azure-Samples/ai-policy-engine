var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

var cosmosDb = cosmos.AddDatabase("aipolicy");

var api = builder.AddProject<Projects.AIPolicyEngine_Api>("aipolicyengine-api")
    .WithReference(redis)
    .WaitFor(redis)
    .WithReference(cosmosDb)
    .WaitFor(cosmos)
    .WithExternalHttpEndpoints();

builder.Build().Run();
