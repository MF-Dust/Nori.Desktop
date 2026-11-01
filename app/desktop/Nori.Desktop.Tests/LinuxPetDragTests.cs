using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Nori.Core.Platform;
using Nori.Desktop.Live2D;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[LinuxX11Fact]
	public Task X11桌宠按下后保留抓取直到松键() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetWindow pet = CreateHitTestPet(fixture);
		IPointer? pointer = null;
		pet.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer, handledEventsToo: true);
		try
		{
			pet.MouseDown(new Point(20, 20), MouseButton.Left);
			Assert.True(pet.IsDragPressed);
			Assert.NotNull(pointer);
			Assert.Same(pet, pointer.Captured);
			Assert.True(ReadPetDragFlag(pet, "_isDragPending"));
			Assert.False(ReadPetDragFlag(pet, "_isNativeDragPending"));
			Assert.False(ReadPetDragFlag(pet, "_isDragging"));

			pet.MouseUp(new Point(20, 20), MouseButton.Left);
			Assert.False(pet.IsDragPressed);
			Assert.Null(pointer.Captured);
			Assert.False(ReadPetDragFlag(pet, "_isDragPending"));
			Assert.Null(fixture._config.Get("pet_window_x"));
		}
		finally { pet.AllowClose = true; pet.Close(); }
		return Task.CompletedTask;
	});

	[LinuxX11Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task X11桌宠拖动在松键或丢失抓取后保存位置(bool loseCapture) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetWindow pet = CreateHitTestPet(fixture);
		IPointer? pointer = null;
		pet.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer, handledEventsToo: true);
		try
		{
			pet.MouseDown(new Point(20, 20), MouseButton.Left);
			Assert.NotNull(pointer);
			Assert.Same(pet, pointer.Captured);
			// 不移动真实桌面的鼠标；模拟已越过阈值的位移，验证两种收尾事件。
			pet.Position = new PixelPoint(200, 300);
			SetPetDragField(pet, "_isDragging", true);
			SetPetDragField(pet, "_hasDragged", true);
			if (loseCapture) pointer.Capture(null);
			else pet.MouseUp(new Point(20, 20), MouseButton.Left);

			Assert.False(pet.IsDragPressed);
			Assert.False(ReadPetDragFlag(pet, "_isDragPending"));
			Assert.False(ReadPetDragFlag(pet, "_isDragging"));
			Assert.False(ReadPetDragFlag(pet, "_hasDragged"));
			await WaitUntilAsync(() => fixture._config.GetStringOr("pet_window_x", "") == "200"
				&& fixture._config.GetStringOr("pet_window_y", "") == "300");
		}
		finally
		{
			pet.MouseUp(new Point(20, 20), MouseButton.Left);
			pet.AllowClose = true;
			pet.Close();
		}
	});

	[LinuxX11Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public Task X11桌宠多键操作按左键状态结束拖动(bool dragging, bool releaseLeftFirst) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetWindow pet = CreateHitTestPet(fixture);
		IPointer? pointer = null;
		pet.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer, handledEventsToo: true);
		try
		{
			pet.MouseDown(new Point(20, 20), MouseButton.Left);
			Assert.NotNull(pointer);
			SetPetDragField(pet, "_isDragging", dragging);
			SetPetDragField(pet, "_hasDragged", dragging);
			// Headless 不保存其他鼠标键状态，必须显式传入仍按住的键。
			pet.MouseDown(new Point(20, 20), MouseButton.Right, RawInputModifiers.LeftMouseButton);
			pet.MouseUp(new Point(20, 20), releaseLeftFirst ? MouseButton.Left : MouseButton.Right,
				releaseLeftFirst ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton);

			Assert.Equal(!releaseLeftFirst, pet.IsDragPressed);
			Assert.Equal(!releaseLeftFirst, ReadPetDragFlag(pet, "_isDragPending"));
			Assert.Equal(dragging && !releaseLeftFirst, ReadPetDragFlag(pet, "_isDragging"));
			Assert.Equal(dragging && !releaseLeftFirst, ReadPetDragFlag(pet, "_hasDragged"));
			if (!releaseLeftFirst) Assert.Same(pet, pointer.Captured);

			pet.MouseUp(new Point(20, 20), releaseLeftFirst ? MouseButton.Right : MouseButton.Left);
			Assert.False(pet.IsDragPressed);
			Assert.Null(pointer.Captured);
			Assert.False(ReadPetDragFlag(pet, "_isDragPending"));
			if (dragging) await WaitUntilAsync(() => fixture._config.Get("pet_window_x") is not null);
		}
		finally
		{
			pet.ContextMenu?.Close();
			pet.MouseUp(new Point(20, 20), MouseButton.Left);
			pet.AllowClose = true;
			pet.Close();
		}
	});

	private static PetWindow CreateHitTestPet(BridgeCommandsTests fixture)
	{
		PetWindow pet = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Pet), fixture._services);
		pet.Show();
		pet.UpdateLayout();
		PetGlControl gl = (PetGlControl)typeof(PetWindow).GetField("_glControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
		// 用确定的采样边界替代 GL 回读，不加载模型或连接显卡。
		typeof(PetGlControl).GetField("_maskBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(gl, new PetHitMask.Bounds(0, 0, PetHitMask.Width - 1, PetHitMask.Height - 1, true));
		return pet;
	}

	private static bool ReadPetDragFlag(PetWindow pet, string name) =>
		(bool)typeof(PetWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;

	private static void SetPetDragField(PetWindow pet, string name, bool value) =>
		typeof(PetWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pet, value);
}

internal sealed class LinuxX11FactAttribute : FactAttribute
{
	public LinuxX11FactAttribute()
	{
		if (!System.OperatingSystem.IsLinux() || PlatformServices.Current.Session != SessionType.X11)
			Skip = "需要可用的 X11 会话（含 XWayland）";
	}
}

internal sealed class LinuxX11TheoryAttribute : TheoryAttribute
{
	public LinuxX11TheoryAttribute()
	{
		if (!System.OperatingSystem.IsLinux() || PlatformServices.Current.Session != SessionType.X11)
			Skip = "需要可用的 X11 会话（含 XWayland）";
	}
}
