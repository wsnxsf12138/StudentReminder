using StudentReminder.Core;
using StudentReminder.Ingestion;

namespace StudentReminder.WxLocal.Adapter;

/// <summary>Creates the in-process WxLocal adapter for <c>wxlocal</c> sources.</summary>
public sealed class WxLocalSourceAdapterFactory(DirectReadCache? cache = null) : ISourceAdapterFactory
{
    public Task<IDataSourceAdapter> CreateAsync(SourceDefinition source, CancellationToken cancellationToken = default)
    {
        if (source.Adapter != "wxlocal")
            throw new ArgumentException($"WxLocal 工厂不支持适配器 {source.Adapter}。");
        return Task.FromResult<IDataSourceAdapter>(new WxLocalSourceAdapter(source, cache));
    }
}
