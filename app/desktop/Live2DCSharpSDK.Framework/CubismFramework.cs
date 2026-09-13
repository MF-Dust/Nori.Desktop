using Live2DCSharpSDK.Framework.Core;
using Live2DCSharpSDK.Framework.Id;

namespace Live2DCSharpSDK.Framework;

/// <summary>
/// Live2D Cubism Original Workflow SDKのエントリポイント
/// 利用開始時はCubismFramework.Initialize()を呼び、CubismFramework.Dispose()で終了する。
/// </summary>
public static class CubismFramework
{
    /// <summary>
    /// メッシュ頂点のオフセット値
    /// </summary>
    public const int VertexOffset = 0;
    /// <summary>
    /// メッシュ頂点のステップ値
    /// </summary>
    public const int VertexStep = 2;

    /// <summary>
    /// IDマネージャのインスタンスを取得する。
    /// </summary>
    public static CubismIdManager CubismIdManager { get; private set; } = new();

    public static bool IsStarted { get; private set; }

	private static readonly object s_runtimeGate = new();
	private static ICubismAllocator? s_allocator;
	private static CubismOption? s_option;
	private static int s_startupCount;

	/// <summary>
	/// 当前持有 Cubism Framework 的渲染控件数量。
	/// </summary>
	public static int ActiveLeaseCount
	{
		get
		{
			lock (s_runtimeGate) return s_startupCount;
		}
	}

	/// <summary>
	/// 串行执行会访问 Cubism 全局状态的操作。
	/// OpenGL 调用仍由调用方在当前 GL 上下文回调内执行。
	/// </summary>
	public static void RunSynchronized(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);
		lock (s_runtimeGate) action();
	}

	/// <summary>
	/// 串行执行会访问 Cubism 全局状态并返回结果的操作。
	/// </summary>
	public static T RunSynchronized<T>(Func<T> action)
	{
		ArgumentNullException.ThrowIfNull(action);
		lock (s_runtimeGate) return action();
	}

	/// <summary>
	/// 首次调用完成初始化；后续调用增加共享租约，每次都必须匹配一次 CleanUp()。
	/// </summary>
	/// <param name="allocator">非托管内存分配器。</param>
	/// <param name="option">日志等框架选项。</param>
	/// <returns>框架已可用时返回 true。</returns>
	public static bool StartUp(ICubismAllocator allocator, CubismOption option)
	{
		lock (s_runtimeGate)
		{
			if (IsStarted)
			{
				s_startupCount++;
				CubismLog.Info("[Live2D SDK]框架已初始化，增加共享租约。");
				return true;
			}

			s_option = option;
			if (s_option != null)
			{
				CubismCore.SetLogFunction(s_option.LogFunction);
			}

			if (allocator == null)
			{
				CubismLog.Warning("[Live2D SDK]框架初始化失败，缺少内存分配器。");
				IsStarted = false;
				return false;
			}

			s_allocator = allocator;
			IsStarted = true;
			s_startupCount = 1;

			// 显示 Live2D Cubism Core 版本信息。
			var version = CubismCore.GetVersion();

			uint major = (version & 0xFF000000) >> 24;
			uint minor = (version & 0x00FF0000) >> 16;
			uint patch = version & 0x0000FFFF;
			uint versionNumber = version;

			CubismLog.Info($"[Live2D SDK]Cubism Core 版本：{major:#0}.{minor:0}.{patch:0000} ({versionNumber})");
			CubismLog.Info("[Live2D SDK]框架初始化完成。");
			return true;
		}
	}

	/// <summary>
	/// 释放一次 StartUp() 租约；只有最后一个渲染控件释放后才清理全局状态。
	/// </summary>
	public static void CleanUp()
	{
		lock (s_runtimeGate)
		{
			if (s_startupCount == 0) return;
			s_startupCount--;
			if (s_startupCount > 0) return;

			IsStarted = false;
			s_allocator = null;
			s_option = null;
		}
	}

    /// <summary>
    /// Core APIにバインドしたログ関数を実行する
    /// </summary>
    /// <param name="data">ログメッセージ</param>
    public static void CoreLogFunction(string data)
    {
        CubismCore.GetLogFunction()?.Invoke(data);
    }

    /// <summary>
    /// 現在のログ出力レベル設定の値を返す。
    /// </summary>
    /// <returns>現在のログ出力レベル設定の値</returns>
    public static LogLevel GetLoggingLevel()
    {
        if (s_option != null)
            return s_option.LoggingLevel;

        return LogLevel.Off;
    }

    public static IntPtr Allocate(int size)
        => s_allocator!.Allocate(size);
    public static IntPtr AllocateAligned(int size, int alignment)
        => s_allocator!.AllocateAligned(size, alignment);
    public static void Deallocate(IntPtr address)
        => s_allocator!.Deallocate(address);
    public static void DeallocateAligned(IntPtr address)
        => s_allocator!.DeallocateAligned(address);
}
