namespace Nori.Desktop.Tests;

/// <summary>原生设置测试共享 Avalonia 与语言状态，不能跨测试类并行修改。</summary>
[CollectionDefinition("Native settings", DisableParallelization = true)]
public sealed class NativeSettingsCollection
{
}
