using Prometheus;
using RobotFleet.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddFleetSources(builder.Environment.ContentRootPath, args);
builder.Services.AddFleetWorker(builder.Configuration);

var port = builder.Configuration.GetValue("Fleet:Management:Port", 8081);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));

var app = builder.Build();
app.MapFleetHealth();
app.MapMetrics();
app.Run();
