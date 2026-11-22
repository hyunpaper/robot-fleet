using RobotFleet.Shared.Tuning;

namespace RobotFleet.Shared.Agent;

/// 14 §2.4. 값의 타당성(범위 · 1회 상한 · 연동 제약 · 인용 실재)은 ControlApi 검증 0~4가 본다(L6)
public sealed record ParameterChange(TuningKey Key, double NewValue);

public sealed record DeadlockProposalDraft(
    string SnapshotId,
    string PromptVersion,
    string AnsweredModel,
    ParameterChange Change,
    ParameterChange? CoupledChange,
    string Rationale,
    IReadOnlyList<string> CitedFactIds);

public sealed record CandidateExplanation(string CandidateId, string Explanation, IReadOnlyList<string> CitedFactIds);

public sealed record InfraExplanationDraft(
    string IncidentId,
    string PromptVersion,
    string AnsweredModel,
    IReadOnlyList<CandidateExplanation> Explanations);
