using System.Numerics;

namespace Nori.Live2D;

/// <summary>模型布局使用行向量矩阵；绘制时先应用布局，再应用投影。</summary>
public static class ModelTransforms
{
	/// <summary>将模型包围框原点映射到纹理分区原点，并生成 UV 与裁剪空间变换。</summary>
	public static (Matrix4x4 Mask, Matrix4x4 Draw) CreateMask(Vector2 modelOrigin, Vector2 textureOrigin, Vector2 scale, bool flipDrawY)
	{
		Matrix4x4 uv = Matrix4x4.CreateTranslation(-modelOrigin.X, -modelOrigin.Y, 0)
			* Matrix4x4.CreateScale(scale.X, scale.Y, 1)
			* Matrix4x4.CreateTranslation(textureOrigin.X, textureOrigin.Y, 0);
		return (uv * Matrix4x4.CreateScale(2, 2, 1) * Matrix4x4.CreateTranslation(-1, -1, 0),
			flipDrawY ? uv * Matrix4x4.CreateScale(1, -1, 1) : uv);
	}

	public static Matrix4x4 CreateLayout(Vector2 canvas, IReadOnlyDictionary<string, float> layout)
	{
		if (!float.IsFinite(canvas.X) || !float.IsFinite(canvas.Y) || canvas.X <= 0 || canvas.Y <= 0)
			throw new ArgumentOutOfRangeException(nameof(canvas), "模型画布尺寸必须是有限正数");
		float scale = 2 / canvas.Y;
		// 同一维度有多个声明时按文件顺序处理；位置始终使用最终缩放尺寸。
		foreach (var entry in layout)
		{
			if (!float.IsFinite(entry.Value)) throw new ArgumentException("模型布局值必须有限", nameof(layout));
			if (entry.Key == "width") scale = entry.Value / canvas.X;
			if (entry.Key == "height") scale = entry.Value / canvas.Y;
		}
		Vector2 size = canvas * scale;
		Vector2 position = Vector2.Zero;
		foreach ((string key, float value) in layout)
		{
			switch (key)
			{
				case "x": case "left": position.X = value; break;
				case "y": case "top": position.Y = value; break;
				case "center_x": position.X = value - size.X / 2; break;
				case "center_y": position.Y = value - size.Y / 2; break;
				case "right": position.X = value - size.X; break;
				case "bottom": position.Y = value - size.Y; break;
			}
		}
		return Matrix4x4.CreateScale(scale, scale, 1) * Matrix4x4.CreateTranslation(position.X, position.Y, 0);
	}
}
