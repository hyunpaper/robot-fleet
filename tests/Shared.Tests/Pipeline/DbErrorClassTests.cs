using FluentAssertions;
using Xunit;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Shared.Tests.Pipeline;

public sealed class DbErrorClassTests
{
    [Theory]
    [InlineData("22001")]   // string_data_right_truncation (데이터)
    [InlineData("22003")]   // numeric_value_out_of_range
    [InlineData("22P02")]   // invalid_text_representation
    [InlineData("23502")]   // not_null_violation (제약)
    [InlineData("23505")]   // unique_violation
    [InlineData("23514")]   // check_violation
    [InlineData("42703")]   // undefined_column (문법 · 접근)
    [InlineData("42P01")]   // undefined_table
    public void 데이터_제약_문법은_결정적(string sqlState) =>
        DbErrorClass.IsDeterministicSqlState(sqlState).Should().BeTrue();

    [Theory]
    [InlineData("08006")]   // connection_failure
    [InlineData("08003")]   // connection_does_not_exist
    [InlineData("53300")]   // too_many_connections (자원)
    [InlineData("57014")]   // query_canceled (관리자 개입)
    [InlineData("40001")]   // serialization_failure — 재시도로 풀린다
    [InlineData("40P01")]   // deadlock_detected
    public void 연결_자원_개입_직렬화는_일시적(string sqlState) =>
        DbErrorClass.IsDeterministicSqlState(sqlState).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    public void SQLSTATE가_없거나_짧으면_일시적(string? sqlState) =>
        DbErrorClass.IsDeterministicSqlState(sqlState).Should().BeFalse();
}
