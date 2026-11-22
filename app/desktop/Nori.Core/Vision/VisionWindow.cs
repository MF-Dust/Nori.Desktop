using Nori.Core.Automation;

namespace Nori.Core.Vision;

/// <summary>读屏与选窗共用的顶层窗口，边界使用屏幕物理像素。</summary>
internal sealed record VisionWindow(nint Handle, string Title, int ProcessId, AutomationBounds Bounds);
