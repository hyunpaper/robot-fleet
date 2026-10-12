namespace RobotFleet.Shared.Geometry;

public enum Heading : byte { North = 0, East = 1, South = 2, West = 3 }

public static class HeadingExtensions
{
    /// <summary>90도 단위 회전 횟수. 0(같음), 1(좌/우), 2(반대).</summary>
    public static int TurnSteps(this Heading from, Heading to)
    {
        var d = Math.Abs((int)from - (int)to) % 4;
        return Math.Min(d, 4 - d);
    }

    public static Heading Opposite(this Heading h) => (Heading)(((int)h + 2) % 4);

    /// <summary>from에서 인접 칸 to로 가는 방향. 인접하지 않으면 예외.</summary>
    public static Heading HeadingTo(Point from, Point to) =>
        (to.X - from.X, to.Y - from.Y) switch
        {
            (0, -1) => Heading.North,
            (1, 0) => Heading.East,
            (0, 1) => Heading.South,
            (-1, 0) => Heading.West,
            _ => throw new ArgumentException($"{from} and {to} are not adjacent"),
        };

    public static char ToChar(this Heading h) => "NESW"[(int)h];
}
