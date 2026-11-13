using StudentReminder.Core;
using StudentReminder.Ingestion;

namespace StudentReminder.QqLocal.Adapter;

/// <summary>Creates the in-process QqLocal adapter for <c>qqlocal</c> sources.</summary>
public sealed class QqLocalSourceAdapterFactory(DirectReadCache? cache = null) : ISourceAdapterFactory
{
    public Task<IDataSourceAdapter> CreateAsync(SourceDefinition source, CancellationToken cancellationToken = default)
    {
        if (source.Adapter != "qqlocal")
            throw new ArgumentException($"QqLocal 工厂不支持适配器 {source.Adapter}。");
        return Task.FromResult<IDataSourceAdapter>(new QqLocalSourceAdapter(source, cache));
    }
}
