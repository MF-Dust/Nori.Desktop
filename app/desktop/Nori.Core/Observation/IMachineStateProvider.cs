namespace Nori.Core.Observation;

/// <summary>
/// 读取这台机器此刻的状态。
///
/// 平台能力不同，取不到的项返回 null 而不是兜底值 —— 调用方要能区分「读数为零」和「读不到」。
/// </summary>
public interface IMachineStateProvider
{
	/// <summary>采一次。实现须自行控制开销：它会在每一轮对话前被调用。</summary>
	MachineState Read();
}
