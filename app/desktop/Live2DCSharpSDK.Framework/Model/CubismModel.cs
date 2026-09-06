using System.Numerics;
using Live2DCSharpSDK.Framework.Core;
using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.Framework.Model;

public class CubismModel : IDisposable
{
    /// <summary>
    /// モデル
    /// </summary>
    public IntPtr Model { get; }

    public readonly List<string> ParameterIds = [];
    public readonly List<string> PartIds = [];
    public readonly List<string> DrawableIds = [];

    /// <summary>
    /// 存在していないパーツの不透明度のリスト
    /// </summary>
    private readonly Dictionary<int, float> _notExistPartOpacities = [];
    /// <summary>
    /// 存在していないパーツIDのリスト
    /// </summary>
    private readonly Dictionary<string, int> _notExistPartId = [];
    /// <summary>
    /// 存在していないパラメータの値のリスト
    /// </summary>
    private readonly Dictionary<int, float> _notExistParameterValues = [];
    /// <summary>
    /// 存在していないパラメータIDのリスト
    /// </summary>
    private readonly Dictionary<string, int> _notExistParameterId = [];
    /// <summary>
    /// 保存されたパラメータ
    /// </summary>
    private readonly List<float> _savedParameters = [];
    /// <summary>
    /// パラメータの値のリスト
    /// </summary>
    private readonly unsafe float* _parameterValues;
    /// <summary>
    /// パラメータの最大値のリスト
    /// </summary>
    private readonly unsafe float* _parameterMaximumValues;
    /// <summary>
    /// パラメータの最小値のリスト
    /// </summary>
    private readonly unsafe float* _parameterMinimumValues;
    /// <summary>
    /// パーツの不透明度のリスト
    /// </summary>
    private readonly unsafe float* _partOpacities;
    /// <summary>
    /// モデルの不透明度
    /// </summary>
    private float _modelOpacity;


    public unsafe CubismModel(IntPtr model)
    {
        Model = model;
        _modelOpacity = 1.0f;

        _parameterValues = CubismCore.GetParameterValues(Model);
        _partOpacities = CubismCore.GetPartOpacities(Model);
        _parameterMaximumValues = CubismCore.GetParameterMaximumValues(Model);
        _parameterMinimumValues = CubismCore.GetParameterMinimumValues(Model);

        {
            var parameterIds = CubismCore.GetParameterIds(Model);
            var parameterCount = CubismCore.GetParameterCount(Model);

            for (int i = 0; i < parameterCount; ++i)
            {
                var str = new string(parameterIds[i]);
                ParameterIds.Add(CubismFramework.CubismIdManager.GetId(str));
            }
        }

        int partCount = CubismCore.GetPartCount(Model);
        var partIds = CubismCore.GetPartIds(Model);

        for (int i = 0; i < partCount; ++i)
        {
            var str = new string(partIds[i]);
            PartIds.Add(CubismFramework.CubismIdManager.GetId(str));
        }

        var drawableIds = CubismCore.GetDrawableIds(Model);
        var drawableCount = CubismCore.GetDrawableCount(Model);

        for (int i = 0; i < drawableCount; ++i)
        {
            var str = new string(drawableIds[i]);
            DrawableIds.Add(CubismFramework.CubismIdManager.GetId(str));
        }
    }

    public void Dispose()
    {
        CubismFramework.DeallocateAligned(Model);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// モデルのパラメータを更新する。
    /// </summary>
    public void Update()
    {
        // Update model.
        CubismCore.UpdateModel(Model);
        // Reset dynamic drawable flags.
        CubismCore.ResetDrawableDynamicFlags(Model);
    }

    /// <summary>
    /// Pixel単位でキャンバスの幅の取得
    /// </summary>
    /// <returns>キャンバスの幅(pixel)</returns>
    public float GetCanvasWidthPixel()
    {
        if (Model == IntPtr.Zero)
        {
            return 0.0f;
        }

        CubismCore.ReadCanvasInfo(Model, out var tmpSizeInPixels, out _, out _);

        return tmpSizeInPixels.X;
    }

    /// <summary>
    /// Pixel単位でキャンバスの高さの取得
    /// </summary>
    /// <returns>キャンバスの高さ(pixel)</returns>
    public float GetCanvasHeightPixel()
    {
        if (new IntPtr(Model) == IntPtr.Zero)
        {
            return 0.0f;
        }

        CubismCore.ReadCanvasInfo(Model, out var tmpSizeInPixels, out _, out _);

        return tmpSizeInPixels.Y;
    }

    /// <summary>
    /// PixelsPerUnitを取得する。
    /// </summary>
    /// <returns>PixelsPerUnit</returns>
    public float GetPixelsPerUnit()
    {
        if (new IntPtr(Model) == IntPtr.Zero)
        {
            return 0.0f;
        }

        CubismCore.ReadCanvasInfo(Model, out _, out _, out var tmpPixelsPerUnit);

        return tmpPixelsPerUnit;
    }

    /// <summary>
    /// Unit単位でキャンバスの幅の取得
    /// </summary>
    /// <returns>キャンバスの幅(Unit)</returns>
    public float GetCanvasWidth()
    {
        CubismCore.ReadCanvasInfo(Model, out var tmpSizeInPixels, out _, out var tmpPixelsPerUnit);

        return tmpSizeInPixels.X / tmpPixelsPerUnit;
    }

    /// <summary>
    /// Unit単位でキャンバスの高さの取得
    /// </summary>
    /// <returns>キャンバスの高さ(Unit)</returns>
    public float GetCanvasHeight()
    {
        CubismCore.ReadCanvasInfo(Model, out var tmpSizeInPixels, out _, out var tmpPixelsPerUnit);

        return tmpSizeInPixels.Y / tmpPixelsPerUnit;
    }

    /// <summary>
    /// パーツのインデックスを取得する。
    /// </summary>
    /// <param name="partId">パーツのID</param>
    /// <returns>パーツのインデックス</returns>
    public int GetPartIndex(string partId)
    {
        int partIndex = PartIds.IndexOf(partId);
        if (partIndex != -1)
        {
            return partIndex;
        }

        int partCount = CubismCore.GetPartCount(Model);

        // モデルに存在していない場合、非存在パーツIDリスト内にあるかを検索し、そのインデックスを返す
        if (_notExistPartId.TryGetValue(partId, out var item))
        {
            return item;
        }

        // 非存在パーツIDリストにない場合、新しく要素を追加する
        partIndex = partCount + _notExistPartId.Count;

        _notExistPartId.TryAdd(partId, partIndex);
        _notExistPartOpacities.Add(partIndex, 0);

        return partIndex;
    }

    /// <summary>
    /// パーツの個数を取得する。
    /// </summary>
    /// <returns>パーツの個数</returns>
    public int GetPartCount()
    {
        return CubismCore.GetPartCount(Model);
    }

    /// <summary>
    /// パーツの不透明度を設定する。
    /// </summary>
    /// <param name="partId">パーツのID</param>
    /// <param name="opacity">不透明度</param>
    public void SetPartOpacity(string partId, float opacity)
    {
        // 高速化のためにPartIndexを取得できる機構になっているが、外部からの設定の時は呼び出し頻度が低いため不要
        int index = GetPartIndex(partId);

        if (index < 0)
        {
            return; // パーツが無いのでスキップ
        }

        SetPartOpacity(index, opacity);
    }

    /// <summary>
    /// パーツの不透明度を設定する。
    /// </summary>
    /// <param name="partIndex">パーツのインデックス</param>
    /// <param name="opacity">パーツの不透明度</param>
    public unsafe void SetPartOpacity(int partIndex, float opacity)
    {
        if (_notExistPartOpacities.ContainsKey(partIndex))
        {
            _notExistPartOpacities[partIndex] = opacity;
            return;
        }

        //インデックスの範囲内検知
        if (0 > partIndex || partIndex >= GetPartCount())
        {
            throw new ArgumentException($"partIndex out of range");
        }

        _partOpacities[partIndex] = opacity;
    }

    /// <summary>
    /// パーツの不透明度を取得する。
    /// </summary>
    /// <param name="partId">パーツのID</param>
    /// <returns>パーツの不透明度</returns>
    public float GetPartOpacity(string partId)
    {
        // 高速化のためにPartIndexを取得できる機構になっているが、外部からの設定の時は呼び出し頻度が低いため不要
        int index = GetPartIndex(partId);

        if (index < 0)
        {
            return 0; //パーツが無いのでスキップ
        }

        return GetPartOpacity(index);
    }

    /// <summary>
    /// パーツの不透明度を取得する。
    /// </summary>
    /// <param name="partIndex">パーツのインデックス</param>
    /// <returns>パーツの不透明度</returns>
    public unsafe float GetPartOpacity(int partIndex)
    {
        if (_notExistPartOpacities.TryGetValue(partIndex, out float value))
        {
            // モデルに存在しないパーツIDの場合、非存在パーツリストから不透明度を返す
            return value;
        }

        //インデックスの範囲内検知
        if (0 > partIndex || partIndex >= GetPartCount())
        {
            throw new ArgumentException($"partIndex out of range");
        }

        return _partOpacities[partIndex];
    }

    /// <summary>
    /// パラメータのインデックスを取得する。
    /// </summary>
    /// <param name="parameterId">パラメータID</param>
    /// <returns>パラメータのインデックス</returns>
    public int GetParameterIndex(string parameterId)
    {
        int parameterIndex = ParameterIds.IndexOf(parameterId);
        if (parameterIndex != -1)
        {
            return parameterIndex;
        }

        // モデルに存在していない場合、非存在パラメータIDリスト内を検索し、そのインデックスを返す
        if (_notExistParameterId.TryGetValue(parameterId, out var data))
        {
            return data;
        }

        // 非存在パラメータIDリストにない場合、新しく要素を追加する
        parameterIndex = CubismCore.GetParameterCount(Model) + _notExistParameterId.Count;

        _notExistParameterId.TryAdd(parameterId, parameterIndex);
        _notExistParameterValues.Add(parameterIndex, 0);

        return parameterIndex;
    }

    /// <summary>
    /// パラメータの個数を取得する。
    /// </summary>
    /// <returns>パラメータの個数</returns>
    public int GetParameterCount()
    {
        return CubismCore.GetParameterCount(Model);
    }

    /// <summary>
    /// パラメータのデフォルト値を取得する。
    /// </summary>
    /// <param name="parameterIndex">パラメータのインデックス</param>
    /// <returns>パラメータのデフォルト値</returns>
    public unsafe float GetParameterDefaultValue(int parameterIndex)
    {
        return CubismCore.GetParameterDefaultValues(Model)[parameterIndex];
    }

    public float GetParameterDefaultValue(string parameterId)
    {
        int index = GetParameterIndex(parameterId);
        return GetParameterDefaultValue(index);
    }

    /// <summary>
    /// パラメータの値を取得する。
    /// </summary>
    /// <param name="parameterId">パラメータID</param>
    /// <returns>パラメータの値</returns>
    public float GetParameterValue(string parameterId)
    {
        // 高速化のためにParameterIndexを取得できる機構になっているが、外部からの設定の時は呼び出し頻度が低いため不要
        int parameterIndex = GetParameterIndex(parameterId);
        return GetParameterValue(parameterIndex);
    }

    /// <summary>
    /// パラメータの値を取得する。
    /// </summary>
    /// <param name="parameterIndex">パラメータのインデックス</param>
    /// <returns>パラメータの値</returns>
    public unsafe float GetParameterValue(int parameterIndex)
    {
        if (_notExistParameterValues.TryGetValue(parameterIndex, out var item))
        {
            return item;
        }

        //インデックスの範囲内検知
        if (0 > parameterIndex || parameterIndex >= GetParameterCount())
        {
            throw new ArgumentException($"parameterIndex out of range");
        }

        return _parameterValues[parameterIndex];
    }

    /// <summary>
    /// パラメータの値を設定する。
    /// </summary>
    /// <param name="parameterId">パラメータID</param>
    /// <param name="value">パラメータの値</param>
    /// <param name="weight">重み</param>
    public void SetParameterValue(string parameterId, float value, float weight = 1.0f)
    {
        int index = GetParameterIndex(parameterId);
        SetParameterValue(index, value, weight);
    }

    /// <summary>
    /// パラメータの値を設定する。
    /// </summary>
    /// <param name="parameterIndex">パラメータのインデックス</param>
    /// <param name="value">パラメータの値</param>
    /// <param name="weight">重み</param>
    public unsafe void SetParameterValue(int parameterIndex, float value, float weight = 1.0f)
    {
        if (_notExistParameterValues.TryGetValue(parameterIndex, out float value1))
        {
            _notExistParameterValues[parameterIndex] = weight == 1
                ? value : (value1 * (1 - weight)) + (value * weight);
            return;
        }

        //インデックスの範囲内検知
        if (0 > parameterIndex || parameterIndex >= GetParameterCount())
        {
            throw new ArgumentException($"parameterIndex out of range");
        }

        if (CubismCore.GetParameterMaximumValues(Model)[parameterIndex] < value)
        {
            value = CubismCore.GetParameterMaximumValues(Model)[parameterIndex];
        }
        if (CubismCore.GetParameterMinimumValues(Model)[parameterIndex] > value)
        {
            value = CubismCore.GetParameterMinimumValues(Model)[parameterIndex];
        }

        _parameterValues[parameterIndex] = (weight == 1)
                                          ? value
                                          : _parameterValues[parameterIndex] = (_parameterValues[parameterIndex] * (1 - weight)) + (value * weight);
    }

    /// <summary>
    /// パラメータの値を加算する。
    /// </summary>
    /// <param name="parameterId">パラメータID</param>
    /// <param name="value">加算する値</param>
    /// <param name="weight">重み</param>
    public void AddParameterValue(string parameterId, float value, float weight = 1.0f)
    {
        int index = GetParameterIndex(parameterId);
        AddParameterValue(index, value, weight);
    }

    /// <summary>
    /// パラメータの値を加算する。
    /// </summary>
    /// <param name="parameterIndex">パラメータのインデックス</param>
    /// <param name="value">加算する値</param>
    /// <param name="weight">重み</param>
    public void AddParameterValue(int parameterIndex, float value, float weight = 1.0f)
    {
        if (parameterIndex == -1)
            return;
        SetParameterValue(parameterIndex, (GetParameterValue(parameterIndex) + (value * weight)));
    }

    /// <summary>
    /// パラメータの値を乗算する。
    /// </summary>
    /// <param name="parameterId">パラメータID</param>
    /// <param name="value">乗算する値</param>
    /// <param name="weight">重み</param>
    public void MultiplyParameterValue(string parameterId, float value, float weight = 1.0f)
    {
        int index = GetParameterIndex(parameterId);
        MultiplyParameterValue(index, value, weight);
    }

    /// <summary>
    /// パラメータの値を乗算する。
    /// </summary>
    /// <param name="parameterIndex">パラメータのインデックス</param>
    /// <param name="value">乗算する値</param>
    /// <param name="weight">重み</param>
    public void MultiplyParameterValue(int parameterIndex, float value, float weight = 1.0f)
    {
        if (parameterIndex == -1)
            return;
        SetParameterValue(parameterIndex, GetParameterValue(parameterIndex) * (1.0f + (value - 1.0f) * weight));
    }

    /// <summary>
    /// Drawableのインデックスを取得する。
    /// </summary>
    /// <param name="drawableId">DrawableのID</param>
    /// <returns>Drawableのインデックス</returns>
    public int GetDrawableIndex(string drawableId)
    {
        return DrawableIds.IndexOf(drawableId);
    }

    /// <summary>
    /// Drawableの個数を取得する。
    /// </summary>
    /// <returns>Drawableの個数</returns>
    public int GetDrawableCount()
    {
        return CubismCore.GetDrawableCount(Model);
    }

    /// <summary>
    /// Drawableの描画順リストを取得する。
    public unsafe int* GetDrawableRenderOrders()
    {
        return CubismCore.GetDrawableRenderOrders(Model);
    }

    /// <summary>
    /// Drawableのテクスチャインデックスを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableのテクスチャインデックス</returns>
    public unsafe int GetDrawableTextureIndex(int drawableIndex)
    {
        var textureIndices = CubismCore.GetDrawableTextureIndices(Model);
        return textureIndices[drawableIndex];
    }

    /// <summary>
    /// Drawableの頂点インデックスの個数を取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの頂点インデックスの個数</returns>
    public unsafe int GetDrawableVertexIndexCount(int drawableIndex)
    {
        return CubismCore.GetDrawableIndexCounts(Model)[drawableIndex];
    }

    /// <summary>
    /// Drawableの頂点の個数を取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの頂点の個数</returns>
    public unsafe int GetDrawableVertexCount(int drawableIndex)
    {
        return CubismCore.GetDrawableVertexCounts(Model)[drawableIndex];
    }

    /// <summary>
    /// Drawableの頂点リストを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの頂点リスト</returns>
    public unsafe float* GetDrawableVertices(int drawableIndex)
    {
        return (float*)GetDrawableVertexPositions(drawableIndex);
    }

    /// <summary>
    /// Drawableの頂点インデックスリストを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの頂点インデックスリスト</returns>
    public unsafe ushort* GetDrawableVertexIndices(int drawableIndex)
    {
        return CubismCore.GetDrawableIndices(Model)[drawableIndex];
    }

    /// <summary>
    /// Drawableの頂点リストを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの頂点リスト</returns>
    public unsafe Vector2* GetDrawableVertexPositions(int drawableIndex)
    {
        return CubismCore.GetDrawableVertexPositions(Model)[drawableIndex];
    }

    /// <summary>
    /// Drawableの頂点のUVリストを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの頂点のUVリスト</returns>
    public unsafe Vector2* GetDrawableVertexUvs(int drawableIndex)
    {
        return CubismCore.GetDrawableVertexUvs(Model)[drawableIndex];
    }

    /// <summary>
    /// Drawableの不透明度を取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableの不透明度</returns>
    public unsafe float GetDrawableOpacity(int drawableIndex)
    {
        return CubismCore.GetDrawableOpacities(Model)[drawableIndex];
    }

    /// <summary>
    /// Drawableのブレンドモードを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableのブレンドモード</returns>
    public unsafe CubismBlendMode GetDrawableBlendMode(int drawableIndex)
    {
        var constantFlags = CubismCore.GetDrawableConstantFlags(Model)[drawableIndex];
        return IsBitSet(constantFlags, CsmEnum.CsmBlendAdditive)
                   ? CubismBlendMode.Additive
                   : IsBitSet(constantFlags, CsmEnum.CsmBlendMultiplicative)
                   ? CubismBlendMode.Multiplicative
                   : CubismBlendMode.Normal;
    }

    /// <summary>
    /// Drawableのマスク使用時の反転設定を取得する。
    /// マスクを使用しない場合は無視される
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableのマスクの反転設定</returns>
    public unsafe bool GetDrawableInvertedMask(int drawableIndex)
    {
        var constantFlags = CubismCore.GetDrawableConstantFlags(Model)[drawableIndex];
        return IsBitSet(constantFlags, CsmEnum.CsmIsInvertedMask);
    }

    /// <summary>
    /// Drawableの表示情報を取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>true    Drawableが表示
    /// false   Drawableが非表示</returns>
    public unsafe bool GetDrawableDynamicFlagIsVisible(int drawableIndex)
    {
        var dynamicFlags = CubismCore.GetDrawableDynamicFlags(Model)[drawableIndex];
        return IsBitSet(dynamicFlags, CsmEnum.CsmIsVisible);
    }

    /// <summary>
    /// 直近のCubismModel::Update関数でDrawableの頂点情報が変化したかを取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>true    Drawableの頂点情報が直近のCubismModel::Update関数で変化した
    /// false   Drawableの頂点情報が直近のCubismModel::Update関数で変化していない</returns>
    public unsafe bool GetDrawableDynamicFlagVertexPositionsDidChange(int drawableIndex)
    {
        var dynamicFlags = CubismCore.GetDrawableDynamicFlags(Model)[drawableIndex];
        return IsBitSet(dynamicFlags, CsmEnum.CsmVertexPositionsDidChange);
    }

    /// <summary>
    /// Drawableのクリッピングマスクリストを取得する。
    /// </summary>
    /// <returns>Drawableのクリッピングマスクリスト</returns>
    public unsafe int** GetDrawableMasks()
    {
        return CubismCore.GetDrawableMasks(Model);
    }

    /// <summary>
    /// Drawableのクリッピングマスクの個数リストを取得する。
    /// </summary>
    /// <returns>Drawableのクリッピングマスクの個数リスト</returns>
    public unsafe int* GetDrawableMaskCounts()
    {
        return CubismCore.GetDrawableMaskCounts(Model);
    }

    /// <summary>
    /// クリッピングマスクを使用しているかどうか？
    /// </summary>
    /// <returns>true    クリッピングマスクを使用している
    /// false   クリッピングマスクを使用していない</returns>
    public unsafe bool IsUsingMasking()
    {
        for (int d = 0; d < CubismCore.GetDrawableCount(Model); ++d)
        {
            if (CubismCore.GetDrawableMaskCounts(Model)[d] <= 0)
            {
                continue;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// 保存されたパラメータを読み込む
    /// </summary>
    public unsafe void LoadParameters()
    {
        int parameterCount = CubismCore.GetParameterCount(Model);
        int savedParameterCount = _savedParameters.Count;

        if (parameterCount > savedParameterCount)
        {
            parameterCount = savedParameterCount;
        }

        for (int i = 0; i < parameterCount; ++i)
        {
            _parameterValues[i] = _savedParameters[i];
        }
    }

    /// <summary>
    /// パラメータを保存する。
    /// </summary>
    public unsafe void SaveParameters()
    {
        int parameterCount = CubismCore.GetParameterCount(Model);
        int savedParameterCount = _savedParameters.Count;

        if (savedParameterCount != parameterCount)
        {
            _savedParameters.Clear();
            for (int i = 0; i < parameterCount; ++i)
            {
                _savedParameters.Add(_parameterValues[i]);
            }
        }
        else
        {
            for (int i = 0; i < parameterCount; ++i)
            {
                _savedParameters[i] = _parameterValues[i];
            }
        }
    }

    /// <summary>
    /// drawableの乗算色を取得する
    /// </summary>
    public unsafe CubismTextureColor GetMultiplyColor(int drawableIndex)
    {
        var color = CubismCore.GetDrawableMultiplyColors(Model)[drawableIndex];
        return new CubismTextureColor(color.X, color.Y, color.Z, color.W);
    }

    /// <summary>
    /// drawableのスクリーン色を取得する
    /// </summary>
    public unsafe CubismTextureColor GetScreenColor(int drawableIndex)
    {
        var color = CubismCore.GetDrawableScreenColors(Model)[drawableIndex];
        return new CubismTextureColor(color.X, color.Y, color.Z, color.W);
    }

    /// <summary>
    /// Drawableのカリング情報を取得する。
    /// </summary>
    /// <param name="drawableIndex">Drawableのインデックス</param>
    /// <returns>Drawableのカリング情報</returns>
    public unsafe bool GetDrawableCulling(int drawableIndex)
    {
        var constantFlags = CubismCore.GetDrawableConstantFlags(Model);
        return !IsBitSet(constantFlags[drawableIndex], CsmEnum.CsmIsDoubleSided);
    }

    /// <summary>
    /// モデルの不透明度を取得する
    /// </summary>
    /// <returns>不透明度の値</returns>
    public float GetModelOpacity()
    {
        return _modelOpacity;
    }

    /// <summary>
    /// モデルの不透明度を設定する
    /// </summary>
    /// <param name="value">不透明度の値</param>
    public void SetModelOpacity(float value)
    {
        _modelOpacity = value;
    }

    private static bool IsBitSet(byte data, byte mask)
    {
        return (data & mask) == mask;
    }

}
