using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.Framework.Physics;

namespace Live2DCSharpSDK.App;

/// <summary>
/// 已在后台完成文件读取、JSON 解析与纹理解码的模型资源。
/// 每个实例只归属一次模型加载，不使用进程级缓存。
/// </summary>
public sealed class LAppModelAssets
{
	private readonly byte[] _mocBytes;
	private readonly IReadOnlyDictionary<string, CubismMotionObj> _motions;
	private readonly IReadOnlyList<LAppTextureAsset> _textures;

	public LAppModelAssets(
		string modelName,
		ModelSettingObj modelSetting,
		byte[] mocBytes,
		CubismPhysicsObj? physics,
		JsonObject? pose,
		IReadOnlyDictionary<string, CubismMotionObj> motions,
		IReadOnlyList<LAppTextureAsset> textures)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
		ArgumentNullException.ThrowIfNull(modelSetting);
		ArgumentNullException.ThrowIfNull(mocBytes);
		ArgumentNullException.ThrowIfNull(motions);
		ArgumentNullException.ThrowIfNull(textures);
		if (mocBytes.Length == 0) throw new ArgumentException("Moc 数据不能为空", nameof(mocBytes));

		ModelName = modelName;
		ModelSetting = modelSetting;
		_mocBytes = [.. mocBytes];
		Physics = physics;
		Pose = pose;
		_motions = new ReadOnlyDictionary<string, CubismMotionObj>(
			motions.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
		_textures = Array.AsReadOnly(textures.ToArray());
	}

	public string ModelName { get; }
	public int MocByteLength => _mocBytes.Length;
	public int MotionCount => _motions.Count;
	public int TextureCount => _textures.Count;
	public IReadOnlyList<LAppTextureAsset> Textures => _textures;
	public bool HasPhysics => Physics is not null;
	public bool HasPose => Pose is not null;

	internal ModelSettingObj ModelSetting { get; }
	internal byte[] MocBytes => _mocBytes;
	internal CubismPhysicsObj? Physics { get; }
	internal JsonObject? Pose { get; }
	internal IReadOnlyDictionary<string, CubismMotionObj> Motions => _motions;
}

/// <summary>已解码且保持模型独占的 RGBA8888 纹理资源。</summary>
public sealed class LAppTextureAsset
{
	private readonly TexturePixels _pixels;

	public LAppTextureAsset(int index, string fileName, TexturePixels pixels)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		ArgumentNullException.ThrowIfNull(pixels);
		if (pixels.Width <= 0 || pixels.Height <= 0)
			throw new ArgumentException("纹理尺寸必须大于零", nameof(pixels));
		int expectedLength = checked(pixels.Width * pixels.Height * 4);
		if (pixels.Data.Length != expectedLength)
			throw new ArgumentException("纹理像素长度与 RGBA8888 尺寸不匹配", nameof(pixels));

		Index = index;
		FileName = fileName;
		_pixels = pixels;
	}

	public int Index { get; }
	public string FileName { get; }
	public int Width => _pixels.Width;
	public int Height => _pixels.Height;
	public int PixelByteLength => _pixels.Data.Length;
	internal TexturePixels Pixels => _pixels;
}
