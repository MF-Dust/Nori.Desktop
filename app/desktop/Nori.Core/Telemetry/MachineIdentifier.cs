using System.Security.Cryptography;
using System.Text;

namespace Nori.Core.Telemetry;

/// <summary>
/// 生成稳定的匿名机器标识,用于遥测用户数量统计。
/// 基于硬件特征生成,不包含任何个人身份信息。
/// </summary>
public static class MachineIdentifier
{
	/// <summary>
	/// 获取当前机器的匿名唯一标识。
	/// 使用机器名 + OS 版本作为种子,生成稳定的哈希值。
	/// </summary>
	public static string GetAnonymousId()
	{
		try
		{
			// 使用机器名和操作系统版本作为稳定种子
			string machineName = Environment.MachineName;
			string osVersion = Environment.OSVersion.VersionString;
			string seed = $"{machineName}:{osVersion}";

			// 生成 SHA256 哈希,取前 16 字节转为十六进制
			byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
			return Convert.ToHexString(hash[..16]).ToLowerInvariant();
		}
		catch
		{
			// 生成失败时使用随机 ID,不影响遥测功能
			return Guid.NewGuid().ToString("N")[..32];
		}
	}
}
