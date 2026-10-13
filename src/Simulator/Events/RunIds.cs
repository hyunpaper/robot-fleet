using System.Globalization;
using System.Security.Cryptography;

namespace RobotFleet.Simulator.Events;

/// run 식별자와 맵 해시(03 SimulationStarted)
public static class RunIds
{
    /// {yyyyMMdd}-{HHmmss}-r{robots} (UTC). 송신 로그 파일명 {mapId}-{runId}.jsonl과 같은 값
    public static string New(DateTimeOffset now, int robots) =>
        now.UtcDateTime.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture) + "-r" + robots.ToString(CultureInfo.InvariantCulture);

    /// "sha256:" + 소문자 hex. 맵 파일 바이트 그대로
    public static string MapHash(byte[] mapFile) => "sha256:" + Convert.ToHexString(SHA256.HashData(mapFile)).ToLowerInvariant();
}
