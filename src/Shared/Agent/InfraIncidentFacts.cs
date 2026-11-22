namespace RobotFleet.Shared.Agent;

/// 인프라 사건 사실 + 규칙 후보. 후보는 규칙이 정하고 LLM은 설명만 붙인다(12). 14 §2.3
public sealed record InfraIncidentFacts(
    string IncidentId,
    InfraIncidentKind Kind,
    DateTimeOffset OpenedAt,
    IReadOnlyList<IncidentFact> Facts,
    IReadOnlyList<InfraCandidate> Candidates);

public enum InfraIncidentKind
{
    QueueBacklog = 1,
    DlqDepth = 2,
    WorkerStopped = 3,
    DbRetry = 4,
    EventLag = 5,
    PodRestart = 6,
    DeadlockUnresolved = 7,
    ReconcileMismatch = 8,
    Failover = 9,
}

public enum InfraFactSource
{
    Prometheus = 1,
    Kubernetes = 2,
    Reconcile = 3,
    FailoverLog = 4,
}

/// 12 인프라 등급 A(I1~I4) · B(B1~B5). 금지 액션은 열거에 없다
public enum InfraActionId
{
    I1 = 1,
    I2 = 2,
    I3 = 3,
    I4 = 4,
    B1 = 5,
    B2 = 6,
    B3 = 7,
    B4 = 8,
    B5 = 9,
}

public sealed record IncidentFact(
    string Id, InfraFactSource Source, string Name, double? Value, string? Unit,
    string? Text, DateTimeOffset ObservedAt);

public sealed record InfraCandidate(string CandidateId, InfraActionId Action, IReadOnlyList<ActionArg> Args, string RuleId);

public sealed record ActionArg(string Name, string Value);
