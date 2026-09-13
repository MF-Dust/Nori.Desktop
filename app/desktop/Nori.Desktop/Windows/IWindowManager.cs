using Avalonia.Controls;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Windows;

/// <summary>
/// 可替换的窗口调度边界
///
/// 定义 BridgeCommands 与托盘所需的窗口查询、显示、关闭、广播与退出能力,
/// 真实实现是 WindowManager, 测试可提供记录调用的替身.
/// </summary>
public interface IWindowManager
{
	/// <summary>建好全部窗口 (不显示)</summary>
	void CreateAll(NoriBridge bridge, AppServices services);

	/// <summary>按标签取窗口, 不存在返回 null</summary>
	Window? Get(string? label);

	/// <summary>按标签取 WebView2 窗口</summary>
	NoriWindow? GetNoriWindow(string? label);

	/// <summary>原生伴侣视窗引用</summary>
	PetWindow? Pet { get; }

	/// <summary>显示窗口；伴侣窗口不抢焦点，其他窗口同时聚焦</summary>
	void Show(string label);

	/// <summary>打开原生设置窗口；可指定设置页，不指定时恢复上次页面。</summary>
	void ShowSettings(string? page = null);

	/// <summary>打开原生记忆窗口；不指定分区时恢复上次页面。</summary>
	void ShowMemory(string? page = null);

	/// <summary>打开原生模型窗口。</summary>
	void ShowModels();

	/// <summary>隐藏窗口</summary>
	void Hide(string label);

	/// <summary>关闭窗口 (真正销毁, 不再复用)</summary>
	void Close(string label);

	/// <summary>切换伴侣视窗显示状态</summary>
	void TogglePet();

	/// <summary>
	/// 窗口当前是否可见 (缓存值, 可在任意线程读取)
	///
	/// Avalonia 的 Window.IsVisible 只能在 UI 线程安全读取, 快照构建不在 UI 线程,
	/// 因此显隐状态由窗口调度缓存一份供快照使用.
	/// </summary>
	bool IsWindowVisible(string label);

	/// <summary>窗口显隐变化 (label, 是否可见); 托盘切换伴侣也会触发</summary>
	event Action<string, bool>? VisibilityChanged;

	/// <summary>向所有 WebView2 窗口广播事件</summary>
	void Broadcast(string name, object? payload);

	/// <summary>在原生伴侣窗口显示临时短句</summary>
	void ShowPetSpeech(string text);

	/// <summary>清除原生伴侣窗口短句</summary>
	void ClearPetSpeech();

	/// <summary>退出应用</summary>
	void Shutdown();
}
