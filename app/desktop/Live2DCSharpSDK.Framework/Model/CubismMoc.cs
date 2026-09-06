// [Nori Modification] Guarded Dispose against native double-free.
using System.Runtime.InteropServices;
using Live2DCSharpSDK.Framework.Core;

namespace Live2DCSharpSDK.Framework.Model;

/// <summary>
/// Mocデータの管理を行うクラス。
/// </summary>
public class CubismMoc : IDisposable
{
    /// <summary>
    /// Mocデータ
    /// </summary>
    private readonly IntPtr _moc;
    public CubismModel Model { get; }

    /// <summary>
    /// バッファからMocファイルを読み取り、Mocデータを作成する。
    /// </summary>
    /// <param name="mocBytes"> Mocファイルのバッファ</param>
    /// <param name="shouldCheckMocConsistency">MOCの整合性チェックフラグ(初期値 : false)</param>
    /// <returns></returns>
    public CubismMoc(byte[] mocBytes, bool shouldCheckMocConsistency = false)
    {
        IntPtr alignedBuffer = CubismFramework.AllocateAligned(mocBytes.Length, CsmEnum.csmAlignofMoc);
        Marshal.Copy(mocBytes, 0, alignedBuffer, mocBytes.Length);

        if (shouldCheckMocConsistency)
        {
            // .moc3の整合性を確認
            bool consistency = CubismCore.HasMocConsistency(alignedBuffer, mocBytes.Length);
            if (!consistency)
            {
                CubismFramework.DeallocateAligned(alignedBuffer);

                // 整合性が確認できなければ処理しない
                throw new Exception("Inconsistent MOC3.");
            }
        }

        var moc = CubismCore.ReviveMocInPlace(alignedBuffer, mocBytes.Length);

        if (moc == IntPtr.Zero)
        {
            throw new Exception("MOC3 is null");
        }

        _moc = moc;

        var modelSize = CubismCore.GetSizeofModel(_moc);
        var modelMemory = CubismFramework.AllocateAligned(modelSize, CsmEnum.CsmAlignofModel);

        var model = CubismCore.InitializeModelInPlace(_moc, modelMemory, modelSize);

        if (model == IntPtr.Zero)
        {
            throw new Exception("MODEL is null");
        }

        Model = new CubismModel(model);
    }

    /// <summary>
    /// デストラクタ。
    /// </summary>
    /// <summary>
    /// [Nori Modification] 重入保护
    ///
    /// 原实现无条件 DeallocateAligned(_moc), 被 Dispose 两次就是非托管内存的双重释放,
    /// 表现为进程直接以 0xC0000374 (STATUS_HEAP_CORRUPTION) 崩溃且没有托管栈。
    /// </summary>
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Model.Dispose();
        CubismFramework.DeallocateAligned(_moc);
        GC.SuppressFinalize(this);
    }
}
