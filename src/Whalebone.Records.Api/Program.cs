using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

using Whalebone.Records.Api;
using Whalebone.Records.Api.Endpoints;
using Whalebone.Records.Api.ExceptionHandling;
using Whalebone.Records.Application;
using Whalebone.Records.Infrastructure;
using Whalebone.Records.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from appsettings, then environment variables
// (Database__ConnectionString), then command-line arguments - last wins.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// The wire format is snake_case: external_id, date_of_birth.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Swashbuckle's schema generator reads Mvc.JsonOptions even for minimal APIs. Without
// this the OpenAPI document would advertise externalId/dateOfBirth while the server
// actually speaks external_id/date_of_birth - drift in the first thing a reader opens.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "Whalebone Records API",
    Version = "v1",
    Description = "Stores and retrieves person records.",
}));

var app = builder.Build();

// Before the first request is served: a host that starts against a schema-less database
// would only serve 500s, so a failure here is fatal by design.
await DatabaseMigrator.MigrateAsync(app.Services).ConfigureAwait(false);

// First in the pipeline, ahead of anything that can throw.
app.UseExceptionHandler();
app.UseStatusCodePages();

// Served in every environment on purpose: the deliverable is a container someone else
// runs, and an unreachable API explorer helps nobody.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Whalebone Records API v1");
    options.DocumentTitle = "Whalebone Records API";
});

// Deliberately no UseHttpsRedirection: the container serves plain HTTP on 8080 and
// terminates TLS at the ingress. Redirecting here would break `curl http://localhost:8080`.
app.MapEndpoints();

app.MapHealthChecks(ApiRoutes.HealthLive, new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(ApiRoutes.HealthReady, new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can drive the real host.</summary>
public partial class Program;
