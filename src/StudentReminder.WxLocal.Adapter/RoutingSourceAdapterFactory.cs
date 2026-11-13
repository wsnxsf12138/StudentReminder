using StudentReminder.Core;
using StudentReminder.Ingestion;

namespace StudentReminder.WxLocal.Adapter;

/// <summary>Routes each local reader to its adapter factory.</summary>
public sealed class RoutingSourceAdapterFactory(
    ISourceAdapterFactory wxlocal, ISourceAdapterFactory qqlocal)
    : ISourceAdapterFactory
{
    public Task<IDataSourceAdapter> CreateAsync(SourceDefinition source, CancellationToken cancellationToken = default) =>
        source.Adapter switch
        {
            "wxlocal" => wxlocal.CreateAsync(source, cancellationToken),
            "qqlocal" => qqlocal.CreateAsync(source, cancellationToken),
            _ => throw new ArgumentException("不支持的数据源。", nameof(source))
        };
}
