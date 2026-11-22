using System.Globalization;
using System.Text;

namespace Nori.Core.Sandbox;

/// <summary>子进程输出的解码与截断。</summary>
public static class SandboxOutput
{
	/// <summary>
	/// 解码子进程输出。
	///
	/// 编码无法事先确定：重定向到管道时没有控制台，各工具链的取值不一致 —— .NET 的
	/// CLI 输出 UTF-8，而 `cmd.exe` 在中文 Windows 上输出 GBK。先按严格 UTF-8 解，
	/// 失败再退到 <see cref="Encoding.Latin1"/>。
	///
	/// 退路选 Latin1 而不是某个具体代码页：它是字节到码位的一一映射，不丢信息，
	/// 非 ASCII 部分显示为乱码但 ASCII 部分（编译错误里的文件名、行号、错误码）完整可读。
	/// 具体代码页需要 <c>System.Text.Encoding.CodePages</c> 包，为这一条引入依赖不划算。
	/// </summary>
	public static string Decode(ReadOnlySpan<byte> bytes)
	{
		try
		{
			return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			return Encoding.Latin1.GetString(bytes);
		}
	}

	/// <summary>
	/// 截断到上限之内，保留首尾两段。
	///
	/// 不能只留头部：构建工具的失败摘要在尾部，只留头部会把「错在哪」丢掉；也不能只留
	/// 尾部：编译错误本身常在中间偏前。首尾各取一半并标注截掉了多少。
	/// </summary>
	public static (string Text, bool Truncated) Fit(string text, int maxCharacters)
	{
		ArgumentNullException.ThrowIfNull(text);
		return Fit(text, text, text.Length, maxCharacters);
	}

	internal static (string Text, bool Truncated) Fit(string head, string tail, long length, int maxCharacters)
	{
		if (maxCharacters <= 0) return ("", length > 0);
		if (length <= maxCharacters) return (head, false);

		// 预留 64 个字符给标注；上限过小时直接保留首尾，不让标注撑破上限。
		bool showMarker = maxCharacters > 64;
		int retained = showMarker ? maxCharacters - 64 : maxCharacters;
		int prefix = (retained + 1) / 2;
		int suffix = retained - prefix;
		string marker = showMarker
			? $"{Environment.NewLine}…… 中间省略 {(length - retained).ToString(CultureInfo.InvariantCulture)} 个字符 ……{Environment.NewLine}"
			: "";

		return (string.Concat(head.AsSpan(0, prefix), marker, tail.AsSpan(tail.Length - suffix)), true);
	}
}
