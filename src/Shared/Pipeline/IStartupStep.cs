namespace RobotFleet.Shared.Pipeline;

/// 기동 시 순서대로 한 번 실행하는 단계(마이그레이션 · 토폴로지 선언 등). 10 §12 "처음 띄울 때"
public interface IStartupStep
{
    /// ReadinessState 구성 요소 이름과 같다
    string Name { get; }

    Task RunAsync(CancellationToken ct);
}
