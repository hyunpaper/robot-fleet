using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

/// TopologySpec.Build(prefix)를 exchange → 큐 → 바인딩 순으로 선언(같은 인자면 멱등). 10 §3.
/// 기존 큐와 인자가 다르면(406) · consistent-hash 플러그인이 없으면 예외 — 자동 삭제하지 않는다
public sealed class TopologyDeclarer : IStartupStep
{
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<TopologyDeclarer> _logger;

    public TopologyDeclarer(RabbitConnectionProvider connections, RabbitMqOptions options, ILogger<TopologyDeclarer> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    public string Name => ReadinessComponents.RabbitTopology;

    public async Task RunAsync(CancellationToken ct)
    {
        var spec = TopologySpec.Build(_options.TopologyPrefix);
        var connection = await _connections.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        string current = "";
        try
        {
            foreach (var e in spec.Exchanges)
            {
                current = $"exchange {e.Name}";
                await channel.ExchangeDeclareAsync(e.Name, e.Type, durable: e.Durable, autoDelete: false, cancellationToken: ct);
            }

            foreach (var q in spec.Queues)
            {
                current = $"queue {q.Name}";
                await channel.QueueDeclareAsync(q.Name, durable: true, exclusive: false, autoDelete: false,
                    arguments: q.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value), cancellationToken: ct);
            }

            foreach (var b in spec.Bindings)
            {
                current = $"binding {b.Exchange} → {b.Queue}";
                await channel.QueueBindAsync(b.Queue, b.Exchange, b.RoutingKey, cancellationToken: ct);
            }
        }
        catch (OperationInterruptedException ex)
        {
            var code = ex.ShutdownReason?.ReplyCode;
            var hint = code switch
            {
                406 => "기존 선언과 인자가 다르다. 메시지가 지워지므로 자동으로 지우지 않는다 — 사람이 판단할 것",
                503 or 540 => "exchange 타입을 모른다 — rabbitmq_consistent_hash_exchange 플러그인 활성화 필요",
                403 => "vhost 권한(configure · write · read) 부족",
                _ => ex.ShutdownReason?.ReplyText ?? ex.Message,
            };
            throw new InvalidOperationException($"토폴로지 선언 실패({current}, {code}): {hint}", ex);
        }

        _logger.LogInformation("토폴로지 선언 완료: exchange {E} · 큐 {Q} · 바인딩 {B} (접두사 '{Prefix}')",
            spec.Exchanges.Count, spec.Queues.Count, spec.Bindings.Count, _options.TopologyPrefix);
    }

    /// 시험 정리용: 이 접두사의 큐 · exchange를 모두 지운다
    internal async Task DeleteAsync(CancellationToken ct)
    {
        var spec = TopologySpec.Build(_options.TopologyPrefix);
        var connection = await _connections.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        foreach (var q in spec.Queues)
            await channel.QueueDeleteAsync(q.Name, ifUnused: false, ifEmpty: false, cancellationToken: ct);
        foreach (var e in spec.Exchanges)
            await channel.ExchangeDeleteAsync(e.Name, ifUnused: false, cancellationToken: ct);
    }
}
