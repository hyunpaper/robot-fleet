using System.Globalization;

namespace RobotFleet.Reconcile;

/// --sendlog 파일|디렉터리 --map mapId [--out json] [--drain-seconds 10]
public sealed record ReconcileOptions(string SendLogPath, string MapId, string? OutPath, int DrainSeconds)
{
    public static ReconcileOptions Parse(string[] args)
    {
        string? sendLog = null;
        string? map = null;
        string? output = null;
        var drainSeconds = 10;

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--sendlog" or "--map" or "--out" or "--drain-seconds"))
            {
                throw new ArgumentException($"알 수 없는 옵션: {option}");
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} 값이 없습니다");
            }

            var value = args[++i];
            switch (option)
            {
                case "--sendlog":
                    sendLog = value;
                    break;
                case "--map":
                    map = value;
                    break;
                case "--out":
                    output = value;
                    break;
                default:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out drainSeconds))
                    {
                        throw new ArgumentException($"--drain-seconds는 0 이상의 정수여야 합니다: {value}");
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(sendLog))
        {
            throw new ArgumentException("--sendlog가 필요합니다");
        }

        if (string.IsNullOrWhiteSpace(map))
        {
            throw new ArgumentException("--map이 필요합니다");
        }

        return new ReconcileOptions(sendLog, map, output, drainSeconds);
    }
}
