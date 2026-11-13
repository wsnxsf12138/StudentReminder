using StudentReminder.Core;
using StudentReminder.Ingestion;
using StudentReminder.QqLocal;
using StudentReminder.QqLocal.Adapter;

namespace StudentReminder.Tests.IntegrationTests;

/// <summary>Contract tests for the in-process QqLocal adapter and its message mapping.</summary>
public sealed class QqLocalSourceAdapterTests
{
    [Fact]
    public void Mapper_MapsGroupTextMessageToNormalizedContract()
    {
        var message = new QqMessage("1000000002", 7, 9001, 1762000000,
            "u_teacher001", "1000000001", "张老师", false, "text", "下周三交实验报告");

        var normalized = QqMessageMapper.Map(message, "qq-local", "1000000002", group: true, DateTimeOffset.UnixEpoch);

        Assert.Equal("qq", normalized.Source);
        Assert.Equal("qqlocal", normalized.Adapter);
        Assert.Equal("qq-local", normalized.AccountId);
        Assert.Equal("1000000002", normalized.ConversationId);
        Assert.Equal("group", normalized.ConversationType);
        Assert.Equal("9001", normalized.MessageId);
        Assert.Equal("1000000001", normalized.SenderId);
        Assert.Equal("张老师", normalized.SenderName);
        Assert.Equal(1762000000, normalized.SentAt.ToUnixTimeSeconds());
        Assert.Equal("text", normalized.ContentType);
        Assert.Equal("下周三交实验报告", normalized.Text);
    }

    [Fact]
    public void Mapper_MapsPrivateMessageWithUidSender()
    {
        var message = new QqMessage("1000000001", 3, 55, 1762000000,
            "u_friend", "", "同学", false, "text", "收到");

        var normalized = QqMessageMapper.Map(message, "qq-local", "1000000001", group: false, DateTimeOffset.UnixEpoch);

        Assert.Equal("private", normalized.ConversationType);
        Assert.Equal("u_friend", normalized.SenderId);
        Assert.Equal("55", normalized.MessageId);
    }

    [Fact]
    public void Mapper_ZeroMsgId_BecomesNullMessageId()
    {
        var message = new QqMessage("1000000002", 1, 0, 1762000000,
            "u_a", "", "", false, "text", "hi");
        Assert.Null(QqMessageMapper.Map(message, "a", "1000000002", true, DateTimeOffset.UnixEpoch).MessageId);
    }

    [Fact]
    public async Task Factory_RejectsNonQqLocalAdapter()
    {
        var factory = new QqLocalSourceAdapterFactory();
        var source = new SourceDefinition { Adapter = "weflow", AccountAlias = "a", Port = 5031 };
        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(source));
    }
}
