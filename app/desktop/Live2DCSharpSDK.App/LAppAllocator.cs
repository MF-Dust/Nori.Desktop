using System.Runtime.InteropServices;
using Live2DCSharpSDK.Framework;

namespace Live2DCSharpSDK.App;

/// <summary>
/// Cubism Framework 的非托管内存分配器。
/// </summary>
public sealed class LAppAllocator : ICubismAllocator
{
	public unsafe IntPtr Allocate(int size) => (IntPtr)NativeMemory.Alloc((nuint)size);

	public unsafe void Deallocate(IntPtr memory) => NativeMemory.Free((void*)memory);

	public unsafe IntPtr AllocateAligned(int size, int alignment) =>
		(IntPtr)NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);

	public unsafe void DeallocateAligned(IntPtr alignedMemory) =>
		NativeMemory.AlignedFree((void*)alignedMemory);
}
