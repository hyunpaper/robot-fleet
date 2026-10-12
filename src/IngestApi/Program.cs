using Prometheus;
using RobotFleet.Hosting;
using RobotFleet.IngestApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddFleetSources(builder.Environment.ContentRootPath, args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
builder.Services.AddFleetIngest(builder.Configuration);

var app = builder.Build();
app.MapPost("/api/v1/events/batch", IngestEndpoints.HandleAsync);
app.MapFleetHealth();
app.MapMetrics();
app.Run();
