// [Nori Modification] Embedded Live2DCSharpSDK with customized Update hooks for desktop pet behaviors.
using System.Text.Json;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Math;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Motion;

namespace Live2DCSharpSDK.App;

public class LAppModel : CubismUserModel
{
    /// <summary>
    /// モデルセッティング情報
    /// </summary>
    private readonly ModelSettingObj _modelSetting;
    /// <summary>
    /// モデルセッティングが置かれたディレクトリ
    /// </summary>
    private readonly string _modelHomeDir;
    /// <summary>
    /// モデルに設定されたまばたき機能用パラメータID
    /// </summary>
    private readonly List<string> _eyeBlinkIds = [];
    /// <summary>
    /// モデルに設定されたリップシンク機能用パラメータID
    /// </summary>
    private readonly List<string> _lipSyncIds = [];
    /// <summary>
    /// 読み込まれているモーションのリスト
    /// </summary>
    private readonly Dictionary<string, ACubismMotion> _motions = [];

    public List<TextureInfo> Textures = [];

    /// <summary>
    /// デルタ時間の積算値[秒]
    /// </summary>
    public float UserTimeSeconds { get; set; }

    public bool RandomMotion { get; set; } = true;
    public bool CustomValueUpdate { get; set; }

    public Action<LAppModel>? ValueUpdate;

    /// <summary>
    /// 在呼吸、物理、口型同步和姿势更新后、模型最终更新前调用。
    /// </summary>
    public Action<LAppModel>? FinalValueUpdate;

    public float DragX => _dragX;
    public float DragY => _dragY;
    public bool IsMotionFinished() => _motionManager == null || _motionManager.IsFinished();

    /// <summary>
    /// 停止所有动作并同步清空当前动作组。
    /// </summary>
    public void StopAllMotions()
    {
        _motionManager.StopAllMotions();
        CurrentMotionGroup = null;
    }

    /// <summary>
    /// [Nori] 当前播放中的动作组名 (未播放时为 null)
    ///
    /// CubismMotionQueueManager 不保留组名, 而桌宠的行为管线要靠"当前是不是待机动作"
    /// 决定是否接管眨眼与眼神微动, 所以在这里记一份.
    /// </summary>
    public string? CurrentMotionGroup { get; private set; }

    /// <summary>
    /// パラメータID: ParamAngleX
    /// </summary>
    public string IdParamAngleX { get; set; }
    /// <summary>
    /// パラメータID: ParamAngleY
    /// </summary>
    public string IdParamAngleY { get; set; }
    /// <summary>
    /// パラメータID: ParamAngleZ
    /// </summary>
    public string IdParamAngleZ { get; set; }
    /// <summary>
    /// パラメータID: ParamBodyAngleX
    /// </summary>
    public string IdParamBodyAngleX { get; set; }
    /// <summary>
    /// パラメータID: ParamEyeBallX
    /// </summary>
    public string IdParamEyeBallX { get; set; }
    /// <summary>
    /// パラメータID: ParamEyeBallXY
    /// </summary>
    public string IdParamEyeBallY { get; set; }

    public string IdParamBreath { get; set; } = CubismFramework.CubismIdManager
        .GetId(CubismDefaultParameterId.ParamBreath);

    /// <summary>
    /// wavファイルハンドラ
    /// </summary>
    //LAppWavFileHandler _wavFileHandler;

    private readonly LAppDelegate _lapp;

    private readonly Random _random = new();

    public LAppModel(LAppDelegate lapp, string dir, string fileName)
    {
        _lapp = lapp;

        if (LAppDefine.MocConsistencyValidationEnable)
        {
            _mocConsistency = true;
        }

        IdParamAngleX = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamAngleX);
        IdParamAngleY = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamAngleY);
        IdParamAngleZ = CubismFramework.CubismIdManager.
            GetId(CubismDefaultParameterId.ParamAngleZ);
        IdParamBodyAngleX = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamBodyAngleX);
        IdParamEyeBallX = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamEyeBallX);
        IdParamEyeBallY = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamEyeBallY);

        _modelHomeDir = dir;

        CubismLog.Debug($"[Live2D App]load model setting: {fileName}");

        using var stream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        _modelSetting = JsonSerializer.Deserialize(stream, ModelSettingObjContext.Default.ModelSettingObj)
            ?? throw new Exception("model3.json error");

        Updating = true;
        Initialized = false;

        //Cubism Model
        var path = _modelSetting.FileReferences?.Moc;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (!File.Exists(path))
            {
                throw new Exception("model is null");
            }

            CubismLog.Debug($"[Live2D App]create model: {path}");

            LoadModel(File.ReadAllBytes(path), _mocConsistency);
        }

        //Physics
        path = _modelSetting.FileReferences?.Physics;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (File.Exists(path))
            {
                LoadPhysics(path);
            }
        }

        //Pose
        path = _modelSetting.FileReferences?.Pose;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (File.Exists(path))
            {
                LoadPose(path);
            }
        }

        LoadBreath();

        if (_modelSetting.Groups is { } groups)
        {
            foreach (var group in groups)
            {
                if (group is null || group.Ids is null)
                {
                    continue;
                }

                if (group.Name == CubismModelSettingJson.EyeBlink)
                {
                    foreach (string? id in group.Ids)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            _eyeBlinkIds.Add(CubismFramework.CubismIdManager.GetId(id));
                        }
                    }
                }
                else if (group.Name == CubismModelSettingJson.LipSync)
                {
                    foreach (string? id in group.Ids)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            _lipSyncIds.Add(CubismFramework.CubismIdManager.GetId(id));
                        }
                    }
                }
            }
        }

        //Layout
        Dictionary<string, float> layout = [];
        _modelSetting.GetLayoutMap(layout);
        ModelMatrix.SetupFromLayout(layout);

        Model.SaveParameters();

        if (_modelSetting.FileReferences?.Motions?.Count > 0)
        {
            foreach (var item in _modelSetting.FileReferences.Motions)
            {
                PreloadMotionGroup(item.Key);
            }
        }

        StopAllMotions();

        Updating = false;
        Initialized = true;
        if (Renderer != null)
        {
            DeleteRenderer();
        }
        Renderer = lapp.CreateRenderer(Model);

        SetupTextures();
    }

    public new void Dispose()
    {
        base.Dispose();

        _motions.Clear();

        foreach (var item in Textures)
        {
            _lapp.TextureManager.ReleaseTexture(item);
        }
        Textures.Clear();
    }

    private void LoadBreath()
    {
        //Breath
        _breath = new()
        {
            Parameters =
            [
                new()
                {
                    ParameterId = IdParamAngleX,
                    Offset = 0.0f,
                    Peak = 15.0f,
                    Cycle = 6.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamAngleY,
                    Offset = 0.0f,
                    Peak = 8.0f,
                    Cycle = 3.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamAngleZ,
                    Offset = 0.0f,
                    Peak = 10.0f,
                    Cycle = 5.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamBodyAngleX,
                    Offset = 0.0f,
                    Peak = 4.0f,
                    Cycle = 15.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamBreath,
                    Offset = 0.5f,
                    Peak = 0.5f,
                    Cycle = 3.2345f,
                    Weight = 0.5f
                }
            ]
        };
    }

    public void Update()
    {
        float deltaTimeSeconds = LAppPal.DeltaTime;
        UserTimeSeconds += deltaTimeSeconds;

        _dragManager.Update(deltaTimeSeconds);
        _dragX = _dragManager.FaceX;
        _dragY = _dragManager.FaceY;

        //-----------------------------------------------------------------
        Model.LoadParameters(); // 前回セーブされた状態をロード
        if (_motionManager.IsFinished() && RandomMotion)
        {
            // モーションの再生がない場合、待機モーションの中からランダムで再生する
            StartRandomMotion(LAppDefine.MotionGroupIdle, MotionPriority.PriorityIdle);
        }
        else
        {
            _motionManager.UpdateMotion(Model, deltaTimeSeconds); // モーションを更新
        }
        Model.SaveParameters(); // 状態を保存

        //-----------------------------------------------------------------

        // 不透明度
        Opacity = Model.GetModelOpacity();

        if (CustomValueUpdate)
        {
            ValueUpdate?.Invoke(this);
        }
        else
        {
            //ドラッグによる変化
            //ドラッグによる顔の向きの調整
            Model.AddParameterValue(IdParamAngleX, _dragX * 30); // -30から30の値を加える
            Model.AddParameterValue(IdParamAngleY, _dragY * 30);
            Model.AddParameterValue(IdParamAngleZ, _dragX * _dragY * -30);

            //ドラッグによる体の向きの調整
            Model.AddParameterValue(IdParamBodyAngleX, _dragX * 10); // -10から10の値を加える

            //ドラッグによる目の向きの調整
            Model.AddParameterValue(IdParamEyeBallX, _dragX); // -1から1の値を加える
            Model.AddParameterValue(IdParamEyeBallY, _dragY);
        }

        // 呼吸など
        _breath?.UpdateParameters(Model, deltaTimeSeconds);

        // 物理演算の設定
        _physics?.Evaluate(Model, deltaTimeSeconds);

        // ポーズの設定
        _pose?.UpdateParameters(Model, deltaTimeSeconds);

        FinalValueUpdate?.Invoke(this);
        Model.Update();
    }

    /// <summary>
    /// モデルを描画する処理。モデルを描画する空間のView-Projection行列を渡す。
    /// </summary>
    /// <param name="matrix">View-Projection行列</param>
    public void Draw(CubismMatrix44 matrix)
    {
        if (Model == null)
        {
            return;
        }

        matrix.MultiplyByMatrix(ModelMatrix);
        if (Renderer != null)
        {
            Renderer.SetMvpMatrix(matrix);
        }

        DoDraw();
    }

    public CubismMotionQueueEntry? StartMotion(string group, int no, MotionPriority priority, FinishedMotionCallback? onFinishedMotionHandler = null)
    {
        if (string.IsNullOrWhiteSpace(group) || no < 0 || !TryGetMotionGroup(group, out var motionGroup, out string resolvedGroup))
        {
            return null;
        }
        if (no >= motionGroup.Count)
        {
            return null;
        }

        var item = motionGroup[no];
        if (item is null || string.IsNullOrWhiteSpace(item.File))
        {
            return null;
        }

        string path;
        try
        {
            path = Path.GetFullPath(_modelHomeDir + item.File);
        }
        catch
        {
            return null;
        }
        if (!File.Exists(path))
        {
            return null;
        }

        // 先确认动作文件可加载, 再预约优先级, 避免无效请求残留 PriorityForce。
        CubismMotion motion;
        string name = $"{resolvedGroup}_{no}";
        try
        {
            if (!_motions.TryGetValue(name, out var value))
            {
                motion = new CubismMotion(path, onFinishedMotionHandler);
                float fadeTime = item.FadeInTime;
                if (fadeTime >= 0.0f)
                {
                    motion.FadeInSeconds = fadeTime;
                }

                fadeTime = item.FadeOutTime;
                if (fadeTime >= 0.0f)
                {
                    motion.FadeOutSeconds = fadeTime;
                }
                motion.SetEffectIds(_eyeBlinkIds, _lipSyncIds);
            }
            else
            {
                motion = (value as CubismMotion)!;
                motion.OnFinishedMotion = onFinishedMotionHandler;
            }
        }
        catch
        {
            return null;
        }

        if (priority == MotionPriority.PriorityForce)
        {
            _motionManager.ReservePriority = priority;
        }
        else if (!_motionManager.ReserveMotion(priority))
        {
            CubismLog.Debug("[Live2D App]can't start motion.");
            return null;
        }

        // [Nori] 模型动作中的 Sound/Voice 不由 SDK 播放, 语音统一由宿主外部音频管线负责。
        CubismLog.Debug($"[Live2D App]start motion: [{resolvedGroup}_{no}]");
        CurrentMotionGroup = resolvedGroup;
        return _motionManager.StartMotionPriority(motion, priority);
    }

    private bool TryGetMotionGroup(
        string group,
        out List<ModelSettingObj.FileReference.Motion> motionGroup,
        out string resolvedGroup)
    {
        motionGroup = [];
        resolvedGroup = "";
        if (string.IsNullOrWhiteSpace(group))
        {
            return false;
        }

        var motions = _modelSetting.FileReferences?.Motions;
        if (motions is null)
        {
            return false;
        }

        if (motions.TryGetValue(group, out var exactGroup) && exactGroup is not null)
        {
            motionGroup = exactGroup;
            resolvedGroup = group;
            return true;
        }

        foreach (var item in motions)
        {
            if (item.Key.Equals(group, StringComparison.OrdinalIgnoreCase) && item.Value is not null)
            {
                motionGroup = item.Value;
                resolvedGroup = item.Key;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ランダムに選ばれたモーションの再生を開始する。
    /// </summary>
    /// <param name="group">モーショングループ名</param>
    /// <param name="priority">優先度</param>
    /// <param name="onFinishedMotionHandler">モーション再生終了時に呼び出されるコールバック関数。NULLの場合、呼び出されない。</param>
    /// <returns>開始したモーションの識別番号を返す。個別のモーションが終了したか否かを判定するIsFinished()の引数で使用する。開始できない時は「-1」</returns>
    public object? StartRandomMotion(string group, MotionPriority priority, FinishedMotionCallback? onFinishedMotionHandler = null)
    {
        if (string.IsNullOrWhiteSpace(group)
            || !TryGetMotionGroup(group, out var motionGroup, out string resolvedGroup)
            || motionGroup.Count == 0)
        {
            return null;
        }

        int no = _random.Next(motionGroup.Count);
        return StartMotion(resolvedGroup, no, priority, onFinishedMotionHandler);
    }

    /// <summary>
    /// イベントの発火を受け取る
    /// </summary>
    /// <param name="eventValue"></param>
    protected override void MotionEventFired(string eventValue)
    {
        CubismLog.Debug($"[Live2D App]{eventValue} is fired on LAppModel!!");
    }

    /// <summary>
    /// 当たり判定テスト。
    /// 指定IDの頂点リストから矩形を計算し、座標が矩形範囲内か判定する。
    /// </summary>
    /// <param name="hitAreaName">当たり判定をテストする対象のID</param>
    /// <param name="x">判定を行うX座標</param>
    /// <param name="y">判定を行うY座標</param>
    /// <returns></returns>
    public bool HitTest(string hitAreaName, float x, float y)
    {
        // 透明時は当たり判定なし。
        if (Opacity < 1)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(hitAreaName) || _modelSetting.HitAreas is null)
        {
            return false;
        }

        foreach (var hitArea in _modelSetting.HitAreas)
        {
            if (hitArea is null || string.IsNullOrWhiteSpace(hitArea.Name) || string.IsNullOrWhiteSpace(hitArea.Id))
            {
                continue;
            }
            if (hitArea.Name.Equals(hitAreaName, StringComparison.OrdinalIgnoreCase))
            {
                var id = CubismFramework.CubismIdManager.GetId(hitArea.Id);
                return IsHit(id, x, y);
            }
        }
        return false; // 存在しない場合はfalse
    }

    /// <summary>
    /// モデルを描画する処理。モデルを描画する空間のView-Projection行列を渡す。
    /// </summary>
    protected void DoDraw()
    {
        if (Model == null)
        {
            return;
        }

        Renderer?.DrawModel();
    }

    /// <summary>
    /// OpenGLのテクスチャユニットにテクスチャをロードする
    /// </summary>
    private void SetupTextures()
    {
        if (_modelSetting.FileReferences?.Textures?.Count > 0)
        {
            for (int index = 0; index < _modelSetting.FileReferences.Textures.Count; index++)
            {
                var texturePath = _modelSetting.FileReferences.Textures[index];
                if (string.IsNullOrWhiteSpace(texturePath))
                    continue;
                texturePath = Path.GetFullPath(_modelHomeDir + texturePath);
                var texture = _lapp.TextureManager.CreateTextureFromPngFile(this, index, texturePath);
                Textures.Add(texture);
            }
        }
    }

    /// <summary>
    /// モーションデータをグループ名から一括でロードする。
    /// モーションデータの名前は内部でModelSettingから取得する。
    /// </summary>
    /// <param name="group">モーションデータのグループ名</param>
    private void PreloadMotionGroup(string group)
    {
        // グループに登録されているモーション数を取得
        var list = _modelSetting.FileReferences.Motions[group];

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            //ex) idle_0
            // モーションのファイル名とパスの取得
            string name = $"{group}_{i}";
            var path = Path.GetFullPath(_modelHomeDir + item.File);

            // モーションデータの読み込み
            var tmpMotion = new CubismMotion(path);

            // フェードインの時間を取得
            float fadeTime = item.FadeInTime;
            if (fadeTime >= 0.0f)
            {
                tmpMotion.FadeInSeconds = fadeTime;
            }

            // フェードアウトの時間を取得
            fadeTime = item.FadeOutTime;
            if (fadeTime >= 0.0f)
            {
                tmpMotion.FadeOutSeconds = fadeTime;
            }
            tmpMotion.SetEffectIds(_eyeBlinkIds, _lipSyncIds);

            if (_motions.ContainsKey(name))
            {
                _motions[name] = tmpMotion;
            }
            else
            {
                _motions.Add(name, tmpMotion);
            }
        }
    }
}
