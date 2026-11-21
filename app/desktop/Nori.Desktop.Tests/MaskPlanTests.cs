using System.Numerics;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class MaskPlanTests
{
	[Fact]
	public void 遮罩顺序不影响共享组且拷贝来源索引()
	{
		int[][] masks = [[], [0, 2], [2, 0], [0]];
		var plan = new MaskPlan(masks);
		Assert.Equal(2, plan.Groups.Count);
		Assert.Null(plan.Drawables[0]);
		Assert.Same(plan.Drawables[1], plan.Drawables[2]);
		Assert.NotSame(plan.Drawables[1], plan.Drawables[3]);
		Assert.Equal(new[] { 1, 2 }, plan.Groups[0].ClippedDrawables);
		masks[1][0] = 3;
		Assert.Equal(new[] { 0, 2 }, plan.Groups[0].Masks);
		Assert.Throws<ArgumentException>(() => new MaskPlan([[-1]]));
		Assert.Throws<ArgumentException>(() => new MaskPlan([[1]]));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void 多纹理分区在容量边界内外都不重叠(int buffers)
	{
		for (int count = 1; count <= 100; count++)
		{
			var tiles = Enumerable.Range(0, count).Select(i => MaskPlan.Place(i, count, buffers)).ToArray();
			foreach (var tile in tiles)
			{
				Assert.InRange(tile.Buffer, 0, buffers - 1);
				Assert.InRange(tile.Channel, 0, 3);
				Assert.InRange(tile.Tile.X, 0, 1 - tile.Tile.Z + 0.000001f);
				Assert.InRange(tile.Tile.Y, 0, 1 - tile.Tile.W + 0.000001f);
				Assert.True(tile.Tile.Z > 0 && tile.Tile.W > 0);
			}
			for (int i = 0; i < tiles.Length; i++)
				for (int j = i + 1; j < tiles.Length; j++)
				{
					if (tiles[i].Buffer != tiles[j].Buffer || tiles[i].Channel != tiles[j].Channel) continue;
					Vector4 a = tiles[i].Tile, b = tiles[j].Tile;
					float overlapX = Math.Min(a.X + a.Z, b.X + b.Z) - Math.Max(a.X, b.X);
					float overlapY = Math.Min(a.Y + a.W, b.Y + b.W) - Math.Max(a.Y, b.Y);
					Assert.True(overlapX < 0.000001f || overlapY < 0.000001f);
				}
		}
	}

	[Fact]
	public void 常用分区保留整图半图与九宫格()
	{
		Assert.Equal((0, 0, new Vector4(0, 0, 1, 1)), MaskPlan.Place(0, 1, 1));
		Assert.Equal((0, 3, new Vector4(0, 0, 1, 1)), MaskPlan.Place(3, 4, 1));
		Assert.Equal((0, 0, new Vector4(0.5f, 0, 0.5f, 1)), MaskPlan.Place(1, 5, 1));
		Assert.Equal((0, 3, new Vector4(2 / 3f, 2 / 3f, 1 / 3f, 1 / 3f)), MaskPlan.Place(35, 36, 1));
	}

	[Fact]
	public void 普通遮罩五百分比边距映射到指定分区()
	{
		var (mask, draw) = MaskPlan.Transform(new(2, 4, 10, 20), new(0.5f, 0.25f, 0.25f, 0.5f), new(256), 100, false);
		AssertVector(new(0.5f, 0.25f), Vector2.Transform(new(1.5f, 3), draw));
		AssertVector(new(0.75f, 0.75f), Vector2.Transform(new(12.5f, 25), draw));
		AssertVector(new(0, -0.5f), Vector2.Transform(new(1.5f, 3), mask));
	}

	[Fact]
	public void 高精度小模型维持像素密度仅溢出方向缩放()
	{
		var (_, draw) = MaskPlan.Transform(new(2, 4, 1, 20), new(0, 0, 1, 1), new(1000), 100, true);
		AssertVector(new(0, 0), Vector2.Transform(new(2, 3), draw));
		AssertVector(new(0.1f, 1), Vector2.Transform(new(3, 25), draw));
	}

	private static void AssertVector(Vector2 expected, Vector2 actual)
	{
		Assert.Equal(expected.X, actual.X, 5);
		Assert.Equal(expected.Y, actual.Y, 5);
	}
}
