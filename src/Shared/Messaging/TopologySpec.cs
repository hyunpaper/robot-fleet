namespace RobotFleet.Shared.Messaging;

public sealed record ExchangeSpec(string Name, string Type, bool Durable);

public sealed record QueueSpec(string Name, EventCategory? Category, int? Partition,
    IReadOnlyDictionary<string, object> Arguments);

public sealed record BindingSpec(string Exchange, string Queue, string RoutingKey);

public sealed record TopologySpec(IReadOnlyList<ExchangeSpec> Exchanges, IReadOnlyList<QueueSpec> Queues,
    IReadOnlyList<BindingSpec> Bindings)
{
    public const int Partitions = 16;

    private static readonly EventCategory[] Categories =
        [EventCategory.Telemetry, EventCategory.Task, EventCategory.Conflict];

    public static TopologySpec Build(string prefix = "")
    {
        var exchanges = new List<ExchangeSpec>();
        var queues = new List<QueueSpec>();
        var bindings = new List<BindingSpec>();

        foreach (var category in Categories)
        {
            var exchange = ExchangeName(category, prefix);
            exchanges.Add(new ExchangeSpec(exchange, "x-consistent-hash", true));

            for (var p = 0; p < Partitions; p++)
            {
                var queue = QueueName(category, p, prefix);
                queues.Add(new QueueSpec(queue, category, p, PartitionArguments(category, prefix)));
                bindings.Add(new BindingSpec(exchange, queue, "1"));
            }
        }

        exchanges.Add(new ExchangeSpec(DlxName(prefix), "fanout", true));
        queues.Add(new QueueSpec(DlqName(prefix), null, null, QuorumOnly()));
        queues.Add(new QueueSpec(InvalidQueueName(prefix), null, null, QuorumOnly()));

        return new TopologySpec(exchanges, queues, bindings);
    }

    public static string ExchangeName(EventCategory category, string prefix = "") => $"{prefix}fleet.{Suffix(category)}";

    public static string QueueName(EventCategory category, int partition, string prefix = "") =>
        $"{prefix}fleet.{Suffix(category)}.p{partition:D2}";

    public static string DlxName(string prefix = "") => $"{prefix}fleet.dlx";

    public static string DlqName(string prefix = "") => $"{prefix}fleet.dlq";

    public static string InvalidQueueName(string prefix = "") => $"{prefix}fleet.invalid";

    private static string Suffix(EventCategory category) => category switch
    {
        EventCategory.Telemetry => "telemetry",
        EventCategory.Task => "task",
        _ => "conflict",
    };

    private static Dictionary<string, object> PartitionArguments(EventCategory category, string prefix) =>
        category == EventCategory.Telemetry
            ? new Dictionary<string, object>
            {
                ["x-queue-type"] = "classic",
                ["x-single-active-consumer"] = true,
                ["x-overflow"] = "drop-head",
                ["x-max-length"] = 20000L,
            }
            : new Dictionary<string, object>
            {
                ["x-queue-type"] = "quorum",
                ["x-single-active-consumer"] = true,
                ["x-delivery-limit"] = 20L,
                ["x-dead-letter-exchange"] = DlxName(prefix),
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-max-length-bytes"] = 67108864L,
            };

    private static Dictionary<string, object> QuorumOnly() => new() { ["x-queue-type"] = "quorum" };
}
