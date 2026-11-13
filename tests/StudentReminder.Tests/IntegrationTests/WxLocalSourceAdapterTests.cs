using StudentReminder.Core;
using StudentReminder.Ingestion;
using StudentReminder.WxLocal;
using StudentReminder.WxLocal.Adapter;
using StudentReminder.QqLocal.Adapter;

namespace StudentReminder.Tests.IntegrationTests;

/// <summary>Contract tests for the in-process WxLocal adapter and its factory routing.</summary>
public sealed class WxLocalSourceAdapterTests
{
    [Fact]
    public void Mapper_MapsTextMessageToNormalizedContract()
    {
        var message = new WxMessage(
            Session: "12345678901@chatroom", LocalId: 7, SortSeq: 42, ServerId: "9001",
            Shard: 0, Type: 1, TypeName: "文本", CreateTime: 1762000000,
            SenderWxid: "wxid_teacher001", Sender: "张老师", SentByMe: false,
            Content: "", Kind: "text", Text: "下周三交实验报告");

        var normalized = WxMessageMapper.Map(message, "wechat-local", "12345678901@chatroom", DateTimeOffset.UnixEpoch);

        Assert.Equal("wechat", normalized.Source);
        Assert.Equal("wxlocal", normalized.Adapter);
        Assert.Equal("wechat-local", normalized.AccountId);
        Assert.Equal("12345678901@chatroom", normalized.ConversationId);
        Assert.Equal("group", normalized.ConversationType);
        Assert.Equal("9001", normalized.MessageId);
        Assert.Equal("wxid_teacher001", normalized.SenderId);
        Assert.Equal("张老师", normalized.SenderName);
        Assert.Equal(1762000000, normalized.SentAt.ToUnixTimeSeconds());
        Assert.Equal("text", normalized.ContentType);
        Assert.Equal("下周三交实验报告", normalized.Text);
    }

    [Fact]
    public void Mapper_ZeroOrEmptyServerId_BecomesNullMessageId()
    {
        var message = new WxMessage("c@chatroom", 1, 1, "0", 0, 1, "文本",
            1762000000, "wxid_a", "", false, "", "text", "hi");
        Assert.Null(WxMessageMapper.Map(message, "a", "c@chatroom", DateTimeOffset.UnixEpoch).MessageId);
    }

    [Fact]
    public async Task Factory_RejectsNonWxLocalAdapter()
    {
        var factory = new WxLocalSourceAdapterFactory();
        var source = new SourceDefinition { Adapter = "weflow", AccountAlias = "a", Port = 5031 };
        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(source));
    }

    [Fact]
    public async Task RoutingFactory_DispatchesToMatchingFactory()
    {
        var router = new RoutingSourceAdapterFactory(new WxLocalSourceAdapterFactory(), new QqLocalSourceAdapterFactory());

        var wx = new SourceDefinition { Adapter = "wxlocal", AccountAlias = "b", Port = 0 };
        var adapter = await router.CreateAsync(wx);
        Assert.IsType<WxLocalSourceAdapter>(adapter);

        var qq = new SourceDefinition { Adapter = "qqlocal", AccountAlias = "c", Port = 0 };
        var qqAdapter = await router.CreateAsync(qq);
        Assert.IsType<QqLocalSourceAdapter>(qqAdapter);

        var weflow = new SourceDefinition { Adapter = "weflow", AccountAlias = "a", Port = 5031 };
        await Assert.ThrowsAsync<ArgumentException>(async () => await router.CreateAsync(weflow));
    }

}
