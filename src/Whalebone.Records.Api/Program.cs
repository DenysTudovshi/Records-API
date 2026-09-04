using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

using Prometheus;

using Whalebone.Records.Api;
using Whalebone.Records.Api.Contracts;
using Whalebone.Records.Api.Correlation;
using Whalebone.Records.Api.Endpoints;
using Whalebone.Records.Api.ExceptionHandling;
using Whalebone.Records.Api.Observability;
using Whalebone.Records.Api.OpenApi;
using Whalebone.Records.Application;
using Whalebone.Records.Infrastructure;
using Whalebone.Records.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// The logs are read by a machine before they are read by a person: this ships as a
// container, and unstructured console text costs a field-by-field parse on ingest.
// IncludeScopes is what carries the correlation id onto every line of a request.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

ServiceMetrics.StartCollecting();

// Configuration comes from appsettings, then environment variables
// (Database__ConnectionString), then command-line arguments - last wins.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// The wire format is snake_case: external_id, date_of_birth.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

    // A malformed uuid or timestamp is a validation failure, not an unreadable request. Left to
    // the binder it becomes a generic 400 that names no field, while a *missing* field gets a
    // precise field-keyed error - the vaguer answer landing on the likelier mistake. These hand
    // back the sentinel the validator already rejects, so both arrive as the same shape.
    //
    // Here and not on Mvc.JsonOptions: this is the options instance minimal API body binding
    // actually uses, and there are no controllers, so registering them there too would be dead
    // configuration. It would not break the OpenAPI document either - Swashbuckle 8.1.4 still
    // emits format uuid/date-time with these registered, which was measured rather than assumed.
    options.SerializerOptions.Converters.Add(new LenientGuidConverter());
    options.SerializerOptions.Converters.Add(new LenientDateTimeOffsetConverter());
});

// Swashbuckle's schema generator reads Mvc.JsonOptions even for minimal APIs. Without
// this the OpenAPI document would advertise externalId/dateOfBirth while the server
// actually speaks external_id/date_of_birth - drift in the first thing a reader opens.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// Deliberately no AddProblemDetails: every error this service writes is the vendor-shaped
// ErrorResponse envelope, written by GlobalExceptionHandler and by the status-code handler below.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Whalebone Records API",
        Version = "v1",
        Description = "Stores and retrieves person records.",
    });

    // Marks the three personal-data fields with x-wb-encrypt, the extension Whalebone use for the
    // same purpose on their own contracts.
    options.SchemaFilter<PersonalDataSchemaFilter>();
});

var app = builder.Build();

// Before the first request is served: a host that starts against a schema-less database
// would only serve 500s, so a failure here is fatal by design.
await DatabaseMigrator.MigrateAsync(app.Services).ConfigureAwait(false);

// Ahead of the exception handler on purpose. The response header works either way -
// it is written from an OnStarting callback - but the log scope does not: registered
// inside, it is already disposed by the time an exception unwinds to the handler, and
// the one log line that most needs a correlation id would be the line without one.
// The cost is that a throw in here escapes UseExceptionHandler, so it only allocates.
app.UseRequestCorrelation();

// Next, ahead of anything that can throw.
// The parameterless overload refuses to start without a fallback, and the only fallback it accepts
// out of the box is AddProblemDetails - which would put a second error shape on the API for the one
// path where GlobalExceptionHandler itself fails. This writes the same envelope instead. It runs
// only if every registered IExceptionHandler declines, which ours never does.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return context.Response.WriteAsJsonAsync(
            FaultResponse.Unexpected("Unexpected error occurred.", context.TraceIdentifier),
            options: null,
            contentType: "application/json");
    },
});

// The statuses the pipeline sets without writing a body: an unrouted path, and a request body the
// binder refused. One error shape across the whole API or it is worse than either shape alone, so
// these carry the same envelope as everything else - message only, since neither names a parameter.
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;

    var message = response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => "The request body could not be read.",
        StatusCodes.Status404NotFound => "No route matches this request.",
        StatusCodes.Status405MethodNotAllowed => "That method is not allowed on this route.",
        StatusCodes.Status415UnsupportedMediaType => "The request media type is not supported.",
        _ => "The request could not be completed.",
    };

    await response.WriteAsJsonAsync(
        ErrorResponse.Plain(message, context.HttpContext.TraceIdentifier),
        options: null,
        contentType: "application/json").ConfigureAwait(false);
});

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

// On the main port, deliberately - see the README. In production a scrape endpoint
// usually gets its own port or a network policy; here it has to be reachable by the
// same one-line quickstart that reaches everything else.
app.MapMetrics();

app.MapHealthChecks(ApiRoutes.HealthLive, new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(ApiRoutes.HealthReady, new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can drive the real host.</summary>
public partial class Program;
