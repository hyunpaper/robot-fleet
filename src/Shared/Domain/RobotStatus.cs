namespace RobotFleet.Shared.Domain;

public enum RobotStatus : byte { Idle = 0, Moving = 1, Waiting = 2, Loading = 3, Unloading = 4, Charging = 5, Error = 6 }
