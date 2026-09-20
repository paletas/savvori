var builder = DistributedApplication.CreateBuilder(args);

var webapi = builder.AddProject<Projects.Savvori_WebApi>("webapi");

builder.AddProject<Projects.Savvori_WebApp>("webapp")
    .WithReference(webapi);

builder.Build().Run();
