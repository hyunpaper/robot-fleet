namespace RobotFleet.Shared.Map;

public enum CellType : byte
{
    Blocked = 0,   // 설비·벽
    Aisle = 1,
    Station = 2,
    Charge = 3,
    Parking = 4,
    // 5 = OneWay 계열은 M7. M1 로더는 '5'를 만나면 검증 실패로 거부한다.
}
