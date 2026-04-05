var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("savvori-postgres-data")
    .WithPgAdmin();

var savvoriDb = postgres.AddDatabase("savvori");

var webapi = builder.AddProject<Projects.Savvori_WebApi>("webapi")
    .WithReference(savvoriDb)
    .WaitFor(savvoriDb);

builder.AddProject<Projects.Savvori_WebApp>("webapp")
    .WithReference(webapi);

builder.Build().Run();
