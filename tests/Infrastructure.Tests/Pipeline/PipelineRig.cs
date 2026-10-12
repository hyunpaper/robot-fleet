// 실제 RabbitMQ · Redis · PostgreSQL로 Ingest · 워커를 조립하는 통합 시험대. 실행 id 접두사 · 스키마로 격리하고 끝나면 지운다
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RobotFleet.EventWorker;
using RobotFleet.Hosting;
using RobotFleet.Infrastructure.Postgres;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.Infrastructure.Redis;
using RobotFleet.Infrastructure.Tests.Postgres;
using RobotFleet.Infrastructure.Tests.RabbitMq;
using RobotFleet.Infrastructure.Tests.Redis;
using RobotFleet.Ingest;
using RobotFleet.IngestApi.Endpoints;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Pipeline;

internal sealed class PipelineRig : IAsyncDisposable
{
    private readonly List<(AssignmentCoordinator Worker, CancellationTokenSource Cts, Task Run)> _workers = [];
    private WebApplication? _ingestHost;

    private PipelineRig(RabbitRig rabbit, RedisRig? redis, PgSchemaFixture? pg)
    {
        Rabbit = rabbit;
        Redis = redis;
        Pg = pg;
        Idem = redis is null ? null
            : new RedisIdemStore(redis.Connections, redis.Options, TimeProvider.System, NullLogger<RedisIdemStore>.Instance);
        Ingest = new IngestService(rabbit.Publisher, TimeProvider.System, NullLogger<IngestService>.Instance, Idem);
        Store = pg is null ? null : new PgEventStore(pg.DataSource, NullLogger<PgEventStore>.Instance);
    }

    public RabbitRig Rabbit { get; }

    public RedisRig? Redis { get; }

    public PgSchemaFixture? Pg { get; }

    public RedisIdemStore? Idem { get; }

    public IngestService Ingest { get; }

    public PgEventStore? Store { get; }

    public static async Task<PipelineRig> CreateAsync(bool redis, bool postgres)
    {
        var rabbit = new RabbitRig();
        await rabbit.Declarer.RunAsync(default);
        PgSchemaFixture? pg = null;
        if (postgres)
        {
            pg = await PgSchemaFixture.CreateAsync();
            await new Migrator(pg.DataSource, PgSchemaFixture.MigrationsDirectory, NullLogger<Migrator>.Instance).ApplyAsync(default);
            await new PartitionMaintainer(pg.DataSource, TimeProvider.System, NullLogger<PartitionMaintainer>.Instance).EnsureAsync(default);
        }

        return new PipelineRig(rabbit, redis ? new RedisRig() : null, pg);
    }

    public WorkerOptions Options(string id) => new()
    {
        WorkerId = id,
        TopologyPrefix = Rabbit.Prefix,
        HeartbeatInterval = TimeSpan.Zero,
        RebalanceInterval = TimeSpan.FromMilliseconds(100),
    };

    public AssignmentCoordinator Worker(string id, bool useRoster = false)
    {
        var options = Options(id);
        var handlers = new EventHandlers(Store!, Rabbit.Publisher, options, NullLogger<EventHandlers>.Instance);
        IPartitionRoster? roster = useRoster
            ? new RedisPartitionRoster(Redis!.Connections, Redis.Options, TimeProvider.System, NullLogger<RedisPartitionRoster>.Instance)
            : null;
        return new AssignmentCoordinator(Rabbit.Subscriber, handlers, options, TimeProvider.System,
            NullLogger<AssignmentCoordinator>.Instance, roster);
    }

    public AssignmentCoordinator StartWorker(string id, bool useRoster = false)
    {
        var worker = Worker(id, useRoster);
        var cts = new CancellationTokenSource();
        _workers.Add((worker, cts, worker.RunAsync(cts.Token)));
        return worker;
    }

    /// 프로세스 사망: ack 없이 채널을 닫는다(미확인분은 브로커가 반환)
    public async Task KillAsync(AssignmentCoordinator worker)
    {
        var entry = _workers.Single(w => w.Worker == worker);
        _workers.Remove(entry);
        await worker.KillAsync();
        await entry.Cts.CancelAsync();
        await entry.Run;
    }

    public async Task StopAsync(AssignmentCoordinator worker)
    {
        var entry = _workers.Single(w => w.Worker == worker);
        _workers.Remove(entry);
        await entry.Cts.CancelAsync();
        await entry.Run;
    }

    public async Task SendAsync(EventBatch batch)
    {
        var response = await Ingest.HandleAsync(FleetJson.Serialize(batch), default);
        if (response.StatusCode != 202) throw new InvalidOperationException($"Ingest {response.StatusCode}: {response.Body.Error}");
    }

    /// 파티션 큐 48개의 대기 메시지 합(미확인 제외)
    public async Task<long> ReadyAsync()
    {
        long total = 0;
        foreach (var category in new[] { EventCategory.Telemetry, EventCategory.Task, EventCategory.Conflict })
        {
            for (var p = 0; p < TopologySpec.Partitions; p++)
                total += await Rabbit.Subscriber.GetMessageCountAsync(TopologySpec.QueueName(category, p, Rabbit.Prefix), default);
        }

        return total;
    }

    public Task<long> CountAsync(string queue) => Rabbit.Subscriber.GetMessageCountAsync(queue, default);

    public Task<long> ScalarAsync(string sql) => Pg!.ScalarAsync(sql);

    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건을 기다리다 시간 초과");
            await Task.Delay(100);
        }
    }

    /// 대기 메시지 0이 2초 이어지면 소비가 끝났다고 본다
    public async Task DrainedAsync(int timeoutMs = 60_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var quietSince = DateTime.MaxValue;
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadyAsync() == 0)
            {
                if (quietSince == DateTime.MaxValue) quietSince = DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(2)) return;
            }
            else
            {
                quietSince = DateTime.MaxValue;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("큐가 비지 않았다");
    }

    /// 실제 Kestrel에 IngestApi 엔드포인트를 띄운다(호스트 조립은 운영과 같은 AddFleetIngest). 반환 = 기본 주소
    public async Task<Uri> StartIngestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RabbitMq"] = TestEnv.RabbitMq,
            ["ConnectionStrings:Redis"] = Redis is null ? "" : TestEnv.Redis,
            ["Fleet:Topology:Prefix"] = Rabbit.Prefix,
            ["Fleet:Redis:KeyPrefix"] = Redis?.Options.KeyPrefix ?? "",
        });
        builder.Services.AddFleetIngest(builder.Configuration);
        _ingestHost = builder.Build();
        _ingestHost.MapPost("/api/v1/events/batch", (HttpContext ctx, IngestService svc) => IngestEndpoints.HandleAsync(ctx, svc));
        await _ingestHost.StartAsync();
        var address = _ingestHost.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address + "/");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cts, run) in _workers)
        {
            await cts.CancelAsync();
            await run;
        }

        if (_ingestHost is not null) await _ingestHost.DisposeAsync();
        await Rabbit.DisposeAsync();
        if (Redis is not null) await Redis.DisposeAsync();
        if (Pg is not null)
        {
            await using var admin = NpgsqlDataSource.Create(TestEnv.Pg!);
            await StaleResourceSweeper.SweepPostgresAsync(admin, DateTimeOffset.UtcNow, default);
            await Pg.DisposeAsync();
        }
    }
}
