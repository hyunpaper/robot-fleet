using FluentAssertions;
using Xunit;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Shared.Tests.Pipeline;

public sealed class DbErrorClassTests
{
    [Theory]
    [InlineData("22001")]   // string_data_right_truncation
    [InlineData("22003")]   // numeric_value_out_of_range
    [InlineData("22P02")]   // invalid_text_representation
    [InlineData("23502")]   // not_null_violation
    [InlineData("23505")]   // unique_violation
    [InlineData("23514")]   // check_violation
    public void 데이터_제약은_Data(string sqlState) =>
        DbErrorClass.Classify(sqlState).Should().Be(StoreErrorKind.Data);

    [Theory]
    [InlineData("42703")]   // undefined_column
    [InlineData("42P01")]   // undefined_table
    [InlineData("42601")]   // syntax_error
    [InlineData("42501")]   // insufficient_privilege
    [InlineData("0A000")]   // feature_not_supported
    [InlineData("25006")]   // 미분류(read_only_sql_transaction) → 시스템
    [InlineData("XX000")]   // internal_error(미분류) → 시스템
    public void 문법_접근_미지원_미분류는_System(string sqlState) =>
        DbErrorClass.Classify(sqlState).Should().Be(StoreErrorKind.System);

    [Theory]
    [InlineData("08006")]   // connection_failure
    [InlineData("08003")]   // connection_does_not_exist
    [InlineData("40001")]   // serialization_failure
    [InlineData("40P01")]   // deadlock_detected
    [InlineData("53300")]   // too_many_connections
    [InlineData("57014")]   // query_canceled
    public void 연결_직렬화_교착_자원_개입은_Transient(string sqlState) =>
        DbErrorClass.Classify(sqlState).Should().Be(StoreErrorKind.Transient);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    public void SQLSTATE가_없거나_짧으면_Transient(string? sqlState) =>
        DbErrorClass.Classify(sqlState).Should().Be(StoreErrorKind.Transient);

    [Fact]
    public void 일시_오류는_재시도() =>
        DbErrorClass.Decide(StoreErrorKind.Transient, batchSize: 10, dlqBudget: 1).Should().Be(StoreAction.Retry);

    [Fact]
    public void 시스템_오류는_정지() =>
        DbErrorClass.Decide(StoreErrorKind.System, batchSize: 10, dlqBudget: 1).Should().Be(StoreAction.Halt);

    [Fact]
    public void 데이터_오류_여러건이면_분할() =>
        DbErrorClass.Decide(StoreErrorKind.Data, batchSize: 8, dlqBudget: 1).Should().Be(StoreAction.Split);

    [Fact]
    public void 데이터_오류_1건이고_예산_있으면_DLQ격리() =>
        DbErrorClass.Decide(StoreErrorKind.Data, batchSize: 1, dlqBudget: 1).Should().Be(StoreAction.IsolateToDlq);

    [Fact]
    public void 데이터_오류_1건이지만_예산_없으면_정지로_전환() =>
        // 배치당 DLQ 상한 초과(2건째 원인) — 매핑 버그 방어로 시스템 오류 취급
        DbErrorClass.Decide(StoreErrorKind.Data, batchSize: 1, dlqBudget: 0).Should().Be(StoreAction.Halt);
}
