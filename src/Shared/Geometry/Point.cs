namespace RobotFleet.Shared.Geometry;

public readonly record struct Point(int X, int Y)
{
    public Point Step(Heading h) => h switch
    {
        Heading.North => new(X, Y - 1),     // 03: Y는 아래로 증가
        Heading.East => new(X + 1, Y),
        Heading.South => new(X, Y + 1),
        Heading.West => new(X - 1, Y),
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };

    public int ManhattanTo(Point other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);

    public override string ToString() => $"({X},{Y})";
}
