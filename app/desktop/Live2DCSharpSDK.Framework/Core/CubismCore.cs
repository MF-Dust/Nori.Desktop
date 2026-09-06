// [Nori Modification] Entry point renamed for Cubism Core 6 (csmGetRenderOrders).
using System.Numerics;
using System.Runtime.InteropServices;

namespace Live2DCSharpSDK.Framework.Core;

public static class CsmEnum
{
    //Alignment constraints.

    /// <summary>
    /// Necessary alignment for mocs (in bytes).
    /// </summary>
    public const int csmAlignofMoc = 64;
    /// <summary>
    /// Necessary alignment for models (in bytes).
    /// </summary>
    public const int CsmAlignofModel = 16;

    //Bit masks for non-dynamic drawable flags.

    /// <summary>
    /// Additive blend mode mask.
    /// </summary>
    public const byte CsmBlendAdditive = 1 << 0;
    /// <summary>
    /// Multiplicative blend mode mask.
    /// </summary>
    public const byte CsmBlendMultiplicative = 1 << 1;
    /// <summary>
    /// Double-sidedness mask.
    /// </summary>
    public const byte CsmIsDoubleSided = 1 << 2;
    /// <summary>
    /// Clipping mask inversion mode mask.
    /// </summary>
    public const byte CsmIsInvertedMask = 1 << 3;

    //Bit masks for dynamic drawable flags.

    /// <summary>
    /// Flag set when visible.
    /// </summary>
    public const byte CsmIsVisible = 1 << 0;
    /// <summary>
    /// Flag set when vertex positions did change.
    /// </summary>
    public const byte CsmVertexPositionsDidChange = 1 << 5;

}

/// <summary>
/// Log handler.
/// </summary>
/// <param name="message">Null-terminated string message to log.</param>
public delegate void LogFunction(string message);

public static partial class CubismCore
{
    //VERSION

    /// <summary>
    /// Queries Core version.
    /// </summary>
    /// <returns>Core version.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetVersion")]
    internal static partial uint GetVersion();

    //CONSISTENCY

    /// <summary>
    /// Checks consistency of a moc.
    /// </summary>
    /// <param name="address">Address of unrevived moc. The address must be aligned to 'csmAlignofMoc'.</param>
    /// <param name="size">Size of moc (in bytes).</param>
    /// <returns>'1' if Moc is valid; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmHasMocConsistency")]
    [return: MarshalAs(UnmanagedType.I4)]
    internal static partial bool HasMocConsistency(IntPtr address, int size);

    //LOGGING

    /// <summary>
    /// Queries log handler.
    /// </summary>
    /// <returns>Log handler.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetLogFunction")]
    internal static partial LogFunction GetLogFunction();

    /// <summary>
    /// Sets log handler.
    /// </summary>
    /// <param name="handler">Handler to use.</param>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmSetLogFunction")]
    internal static partial void SetLogFunction(LogFunction handler);

    //MOC

    /// <summary>
    /// Tries to revive a moc from bytes in place.
    /// </summary>
    /// <param name="address">Address of unrevived moc. The address must be aligned to 'csmAlignofMoc'.</param>
    /// <param name="size">Size of moc (in bytes).</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmReviveMocInPlace")]
    internal static partial IntPtr ReviveMocInPlace(IntPtr address, int size);

    //MODEL

    /// <summary>
    /// Queries size of a model in bytes.
    /// </summary>
    /// <param name="moc">Moc to query.</param>
    /// <returns>Valid size on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetSizeofModel")]
    internal static partial int GetSizeofModel(IntPtr moc);

    /// <summary>
    /// Tries to instantiate a model in place.
    /// </summary>
    /// <param name="moc">Source moc.</param>
    /// <param name="address">Address to place instance at. Address must be aligned to 'csmAlignofModel'.</param>
    /// <param name="size">Size of memory block for instance (in bytes).</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmInitializeModelInPlace")]
    internal static partial IntPtr InitializeModelInPlace(IntPtr moc, IntPtr address, int size);

    /// <summary>
    /// Updates a model.
    /// </summary>
    /// <param name="model">Model to update.</param>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmUpdateModel")]
    internal static partial void UpdateModel(IntPtr model);

    //CANVAS

    /// <summary>
    /// Reads info on a model canvas.
    /// </summary>
    /// <param name="model">Model query.</param>
    /// <param name="outSizeInPixels">Canvas dimensions.</param>
    /// <param name="outOriginInPixels">Origin of model on canvas.</param>
    /// <param name="outPixelsPerUnit">Aspect used for scaling pixels to units.</param>
    [DllImport("Live2DCubismCore", EntryPoint = "csmReadCanvasInfo")]
#pragma warning disable SYSLIB1054 // 使用 “LibraryImportAttribute” 而不是 “DllImportAttribute” 在编译时生成 P/Invoke 封送代码
    internal extern static void ReadCanvasInfo(IntPtr model, out Vector2 outSizeInPixels,
#pragma warning restore SYSLIB1054 // 使用 “LibraryImportAttribute” 而不是 “DllImportAttribute” 在编译时生成 P/Invoke 封送代码
        out Vector2 outOriginInPixels, out float outPixelsPerUnit);

    //PARAMETERS

    /// <summary>
    /// Gets number of parameters.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid count on success; '-1' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetParameterCount")]
    internal static partial int GetParameterCount(IntPtr model);

    /// <summary>
    /// Gets parameter IDs.
    /// All IDs are null-terminated ANSI strings.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetParameterIds")]
    internal static unsafe partial sbyte** GetParameterIds(IntPtr model);

    /// <summary>
    /// Gets parameter maximum values.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetParameterMaximumValues")]
    internal static unsafe partial float* GetParameterMaximumValues(IntPtr model);

    /// <summary>
    /// Gets minimum parameter values.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetParameterMinimumValues")]
    internal static unsafe partial float* GetParameterMinimumValues(IntPtr model);

    /// <summary>
    /// Gets default parameter values.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetParameterDefaultValues")]
    internal static unsafe partial float* GetParameterDefaultValues(IntPtr model);

    /// <summary>
    /// Gets read/write parameter values buffer.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetParameterValues")]
    internal static unsafe partial float* GetParameterValues(IntPtr model);

    //PARTS

    /// <summary>
    /// Gets number of parts.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid count on success; '-1' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetPartCount")]
    internal static partial int GetPartCount(IntPtr model);

    /// <summary>
    /// Gets parts IDs.
    /// All IDs are null-terminated ANSI strings.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetPartIds")]
    internal static unsafe partial sbyte** GetPartIds(IntPtr model);

    /// <summary>
    /// Gets read/write part opacities buffer.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetPartOpacities")]
    internal static unsafe partial float* GetPartOpacities(IntPtr model);

    //DRAWABLES

    /// <summary>
    /// Gets number of drawables.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid count on success; '-1' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableCount")]
    internal static partial int GetDrawableCount(IntPtr model);

    /// <summary>
    /// Gets drawable IDs.
    /// All IDs are null-terminated ANSI strings.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableIds")]
    internal static unsafe partial sbyte** GetDrawableIds(IntPtr model);

    /// <summary>
    /// Gets constant drawable flags.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableConstantFlags")]
    internal static unsafe partial byte* GetDrawableConstantFlags(IntPtr model);

    /// <summary>
    /// Gets dynamic drawable flags.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableDynamicFlags")]
    internal static unsafe partial byte* GetDrawableDynamicFlags(IntPtr model);

    /// <summary>
    /// Gets drawable texture indices.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableTextureIndices")]
    internal static unsafe partial int* GetDrawableTextureIndices(IntPtr model);

    /// <summary>
    /// Gets drawable render orders.
    /// The higher the order, the more up front a drawable is.
    ///
    /// [Nori Modification] Cubism Core 6 renamed this export from
    /// "csmGetDrawableRenderOrders" to "csmGetRenderOrders"; binding the old name
    /// throws EntryPointNotFoundException on the very first frame that draws a model.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0'otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetRenderOrders")]
    internal static unsafe partial int* GetDrawableRenderOrders(IntPtr model);

    /// <summary>
    /// Gets drawable opacities.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableOpacities")]
    internal static unsafe partial float* GetDrawableOpacities(IntPtr model);

    /// <summary>
    /// Gets numbers of masks of each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableMaskCounts")]
    internal static unsafe partial int* GetDrawableMaskCounts(IntPtr model);

    /// <summary>
    /// Gets number of vertices of each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableMasks")]
    internal static unsafe partial int** GetDrawableMasks(IntPtr model);

    /// <summary>
    /// Gets number of vertices of each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableVertexCounts")]
    internal static unsafe partial int* GetDrawableVertexCounts(IntPtr model);

    /// <summary>
    /// Gets vertex position data of each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; a null pointer otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableVertexPositions")]
    internal static unsafe partial Vector2** GetDrawableVertexPositions(IntPtr model);

    /// <summary>
    /// Gets texture coordinate data of each drawables.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableVertexUvs")]
    internal static unsafe partial Vector2** GetDrawableVertexUvs(IntPtr model);

    /// <summary>
    /// Gets number of triangle indices for each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableIndexCounts")]
    internal static unsafe partial int* GetDrawableIndexCounts(IntPtr model);

    /// <summary>
    /// Gets triangle index data for each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableIndices")]
    internal static unsafe partial ushort** GetDrawableIndices(IntPtr model);

    /// <summary>
    /// Gets multiply color data for each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableMultiplyColors")]
    internal static unsafe partial Vector4* GetDrawableMultiplyColors(IntPtr model);

    /// <summary>
    /// Gets screen color data for each drawable.
    /// </summary>
    /// <param name="model">Model to query.</param>
    /// <returns>Valid pointer on success; '0' otherwise.</returns>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmGetDrawableScreenColors")]
    internal static unsafe partial Vector4* GetDrawableScreenColors(IntPtr model);

    /// <summary>
    /// Resets all dynamic drawable flags.
    /// </summary>
    /// <param name="model">Model containing flags.</param>
    [LibraryImport("Live2DCubismCore", EntryPoint = "csmResetDrawableDynamicFlags")]
    internal static unsafe partial void ResetDrawableDynamicFlags(IntPtr model);
}