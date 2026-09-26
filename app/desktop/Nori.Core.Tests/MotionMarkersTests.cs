using System.IO.Compression;
using Nori.Core.Chat;
using Nori.Core.Resources;

namespace Nori.Core.Tests;

/// <summary>
/// 动作标记解析, 对应 Rust 版 chat.rs 的 extract_motion_markers
/// </summary>
public class MotionMarkersTests
{
	[Fact]
	public void 剥离单个标记()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("你好呀。\n[nori_motion:smile]");
		Assert.Equal("你好呀。\n", content);
		Assert.Equal(["smile"], motions);
	}

	[Fact]
	public void 剥离多个标记并保留其余文本()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("A[nori_motion:a]B[nori_motion:b]C");
		Assert.Equal("ABC", content);
		Assert.Equal(["a", "b"], motions);
	}

	[Fact]
	public void 标记名去空白()
	{
		(_, IReadOnlyList<string> motions) = MotionMarkers.Extract("[nori_motion:  smile  ]");
		Assert.Equal(["smile"], motions);
	}

	[Fact]
	public void 空标记名被忽略()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("A[nori_motion:]B");
		Assert.Equal("AB", content);
		Assert.Empty(motions);
	}

	[Fact]
	public void 未闭合的标记原样保留()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("A[nori_motion:smile");
		Assert.Equal("A[nori_motion:smile", content);
		Assert.Empty(motions);
	}

	[Fact]
	public void 没有标记时原样返回()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("普通回复");
		Assert.Equal("普通回复", content);
		Assert.Empty(motions);
	}
}
