using System.Numerics;
using System.Runtime.ExceptionServices;
using Nori.Live2D;
using Nori.Desktop.Live2D.Gl;

namespace Nori.Desktop.Live2D;

/// <summary>后台解码后的宿主 RGBA8888 数据，不携带 SDK 或 GL 对象。</summary>
public sealed record TexturePixels(int Width, int Height, byte[] Data);

/// <summary>模型纹理槽及其独占的预载像素。</summary>
public sealed record PreparedTexture(int Index, string FileName, TexturePixels Pixels)
{
	public int Width => Pixels.Width;
	public int Height => Pixels.Height;
	public int PixelByteLength => Pixels.Data.Length;
}

/// <summary>只包含准备阶段的数据；提交到 GL 时不再访问文件。</summary>
public sealed record PreparedModelData(
	string ModelName,
	ModelDefinition Definition,
	ReadOnlyMemory<byte> Moc,
	PhysicsDefinition? Physics,
	PoseDefinition? Pose,
	IReadOnlyDictionary<string, MotionClip> Motions,
	IReadOnlyList<PreparedTexture> Textures)
{
	public int MocByteLength => Moc.Length;
	public int MotionCount => Motions.Count;
	public int TextureCount => Textures.Count;
	public bool HasPhysics => Physics is not null;
	public bool HasPose => Pose is not null;
}

/// <summary>每个宠物/预览实例独占动画、指针及 GL 资源，不经过旧 SDK manager。</summary>
public sealed class NativeModelHost : IDisposable
{
	private readonly PointerSmoother _pointer = new();
	private bool _disposed;
	public AnimatedModel Animation { get; }
	public NativeModel Model => Animation.Model;
	public NativeGlRenderer Renderer { get; }
	public Matrix4x4 ModelMatrix { get; }
	/// <summary>窗口布局只读取快照，不在 UI 线程借用可能被替换的原生模型。</summary>
	public Vector2 CanvasSize { get; }
	public Vector2 Pointer => _pointer.Position;
	internal bool HasPointerInput { get; private set; }

	public NativeModelHost(OpenGLApi gl, PreparedModelData data)
	{
		Animation = new(data.Moc.Span, data.Definition, data.Motions, data.Physics, data.Pose);
		try
		{
			CanvasSize = Model.CanvasSize;
			ModelMatrix = ModelTransforms.CreateLayout(CanvasSize / Model.PixelsPerUnit, data.Definition.Layout);
			Renderer = new(gl, Model, data.Textures);
		}
		catch
		{
			Animation.Dispose();
			throw;
		}
	}

	public void SetDragging(float x, float y)
	{
		_pointer.SetTarget(new(x, y));
		HasPointerInput = true;
	}

	public void Update(float deltaSeconds)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_pointer.Update(deltaSeconds);
		Animation.Update(deltaSeconds);
	}

	public bool HitTest(string area, float x, float y) => Matrix4x4.Invert(ModelMatrix, out var inverse)
		&& Animation.HitTest(area, Vector2.Transform(new(x, y), inverse));

	public void Draw(Matrix4x4 projection) => Renderer.Draw(ModelMatrix * projection);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Exception? failure = null;
		try { Renderer.Dispose(); }
		catch (Exception exception) { failure = exception; }
		finally { Animation.Dispose(); }
		if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
	}
}
