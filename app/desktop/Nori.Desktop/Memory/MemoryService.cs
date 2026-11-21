using Avalonia.Controls;
using Nori.Core.Memory;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Memory;

/// <summary>原生记忆窗口的宿主服务。</summary>
public sealed class MemoryService(AppServices services, Window owner)
	: NativeBridgeServiceBase(services, new WindowContext(owner, WindowLabels.Memory))
{
	protected override string StateNotificationFailureMessage => "记忆窗口状态通知失败";

	protected override bool IsStateChangingCommand(string command) => IsMutatingCommand(command);

	/// <summary>后台分页读取记忆时间线。关闭或新筛选会取消尚未返回的读取。</summary>
	public Task<MemoryTimelinePage> QueryTimelineAsync(MemoryTimelineQuery query, TimeZoneInfo zone, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentNullException.ThrowIfNull(zone);
		return RunBackgroundAsync(token => RuntimeMemory.Store.QueryTimeline(query, zone, token), cancellationToken);
	}

	/// <summary>后台读取一条时间线详情，只返回库中已有的字段。</summary>
	public Task<MemoryTimelineDetail> GetTimelineDetailAsync(long id, TimeZoneInfo zone, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(zone);
		return RunBackgroundAsync(token => RuntimeMemory.GetTimelineDetail(id, zone, token), cancellationToken);
	}

	private Nori.Core.Memory.MemoryService RuntimeMemory =>
		(Services.Runtime ?? throw new InvalidOperationException("应用运行时尚未就绪")).Memory;

	protected override bool IsBackgroundCommand(string command) => command is
		"memory_list_page" or "memory_get" or "memory_atom_list"
		or "memory_knowledge_status" or "memory_knowledge_reindex" or "memory_recall_debug"
		or "memory_reembed_all" or "memory_export" or "memory_import_preview";

	internal static bool IsMutatingCommand(string command) => command is
		"memory_add" or "memory_update" or "memory_delete" or "memory_clear" or "memory_archive"
		or "memory_restore" or "memory_knowledge_reindex" or "memory_update_settings" or "memory_reembed_all" or "memory_import_commit";
}
