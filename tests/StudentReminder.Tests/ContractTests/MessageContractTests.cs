using System.Text.Json;
using StudentReminder.Core;

namespace StudentReminder.Tests.ContractTests;

public sealed class MessageContractTests
{
    [Fact]
    public void PlanMessageProtocolRoundTripsChineseEvidenceAndTimezone()
    {
        const string json = """
            {"source":"wechat","adapter":"weflow","account_id":"local-account-01",
             "conversation_id":"course-group-001","conversation_name":"高数课程群",
             "sender_id":"member-02","sent_at":"2026-11-10T09:00:00+08:00",
             "received_at":"2026-11-10T09:00:02+08:00","text":"本周五18点前提交实验报告"}
            """;
        var message = JsonSerializer.Deserialize<NormalizedMessage>(json, ContractJson.Options)!;
        Assert.Equal("本周五18点前提交实验报告", message.Text);
        Assert.Equal(TimeSpan.FromHours(8), message.SentAt.Offset);
        var serialized = JsonSerializer.Serialize(message, ContractJson.Options);
        Assert.Contains("\"account_id\"", serialized);
        var restored = JsonSerializer.Deserialize<NormalizedMessage>(serialized, ContractJson.Options)!;
        Assert.Equal(message.Text, restored.Text);
        Assert.Equal(message.SentAt, restored.SentAt);
    }

    [Fact]
    public void CandidateKeepsNullableTimeAndMultipleEvidenceReferences()
    {
        var candidate = new EventCandidate
        {
            Title = "待通知的考试", Category = "exam", Action = "等待正式通知",
            Evidence = [new MessageEvidence("qq", "course-a", "message-a") { AccountId = "local-alias" },
                new MessageEvidence("wechat", "course-b", "message-b")],
            Ambiguities = ["未提供日期"]
        };
        var json = JsonSerializer.Serialize(candidate, ContractJson.Options);
        var restored = JsonSerializer.Deserialize<EventCandidate>(json, ContractJson.Options)!;
        Assert.Null(restored.DeadlineAt);
        Assert.True(restored.NeedsConfirmation);
        Assert.Equal("uncertain", restored.Certainty);
        Assert.Equal(2, restored.Evidence.Count);
        Assert.Equal("local-alias", restored.Evidence[0].AccountId);
        Assert.Equal("未提供日期", Assert.Single(restored.Ambiguities));
    }

    [Theory]
    [InlineData("2026-11-10T09:00:00")]
    [InlineData("明天")]
    [InlineData("2026-99-10T09:00:00+08:00")]
    public void JsonTimeRequiresAnExplicitOffset(string timestamp)
    {
        var json = JsonSerializer.Serialize(IntegrationTests.ImportTests.Example(), ContractJson.Options);
        using var document = JsonDocument.Parse(json);
        var validTimestamp = document.RootElement.GetProperty("sent_at").GetString()!;
        // Replace the JSON-encoded value because '+' may be escaped by the encoder.
        json = json.Replace(JsonSerializer.Serialize(validTimestamp), JsonSerializer.Serialize(timestamp), StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NormalizedMessage>(json, ContractJson.Options));
    }

    [Fact]
    public void UnknownFieldsAreRejectedRatherThanSilentlyIgnored()
    {
        var json = JsonSerializer.Serialize(IntegrationTests.ImportTests.Example(), ContractJson.Options);
        json = json.Insert(1, "\"unexpected_field\":true,");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NormalizedMessage>(json, ContractJson.Options));
    }
}
