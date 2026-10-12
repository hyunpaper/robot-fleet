namespace RobotFleet.Shared.Map;

public sealed class MapValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public MapValidationException(IReadOnlyList<string> errors)
        : base($"Map validation failed with {errors.Count} error(s):\n  " + string.Join("\n  ", errors)) => Errors = errors;
}
