namespace RobotFleet.Shared.Geometry;

/// <summary>경로의 한 칸. Heading은 이 칸에 "진입한 방향"(= 이 칸에서 바라보는 방향).</summary>
public readonly record struct PathStep(Point Cell, Heading Heading);
