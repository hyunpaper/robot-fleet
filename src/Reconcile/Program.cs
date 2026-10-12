using Microsoft.Extensions.Configuration;
using RobotFleet.Reconcile;

ReconcileOptions options;
try
{
    options = ReconcileOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("사용: --sendlog <파일|디렉터리> --map <mapId> [--out <json>] [--drain-seconds 10]");
    return 2;
}

// appsettings.json → appsettings.Local.json(없으면 건너뜀) → 환경 변수
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString = config["ConnectionStrings:Postgres"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("appsettings.Local.json 또는 ConnectionStrings__Postgres 필요");
    return 2;
}

return await new ReconcileRunner(new ReconcileQueries(), Console.Out)
    .RunAsync(options, connectionString, CancellationToken.None);
