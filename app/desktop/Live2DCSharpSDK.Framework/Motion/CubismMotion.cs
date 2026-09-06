using System.Text.Json;
using Live2DCSharpSDK.Framework.Math;
using Live2DCSharpSDK.Framework.Model;

namespace Live2DCSharpSDK.Framework.Motion;

/// <summary>
/// モーションのクラス。
/// </summary>
public class CubismMotion : ACubismMotion
{
    public const string EffectNameEyeBlink = "EyeBlink";
    public const string EffectNameLipSync = "LipSync";
    public const string TargetNameModel = "Model";
    public const string TargetNameParameter = "Parameter";
    public const string TargetNamePartOpacity = "PartOpacity";

    // Id
    public const string IdNameOpacity = "Opacity";

    /// <summary>
    /// mtnファイルで定義される一連のモーションの長さ
    /// </summary>
    private readonly float _loopDurationSeconds;
    /// <summary>
    /// ループするか?
    /// </summary>
    public bool IsLoop { get; set; }
    /// <summary>
    /// ループ時にフェードインが有効かどうかのフラグ。初期値では有効。
    /// </summary>
    public bool IsLoopFadeIn { get; set; }
    /// <summary>
    /// 実際のモーションデータ本体
    /// </summary>
    private readonly CubismMotionData _motionData;

    /// <summary>
    /// 自動まばたきを適用するパラメータIDハンドルのリスト。  モデル（モデルセッティング）とパラメータを対応付ける。
    /// </summary>
    private List<string> _eyeBlinkParameterIds;
    /// <summary>
    /// リップシンクを適用するパラメータIDハンドルのリスト。  モデル（モデルセッティング）とパラメータを対応付ける。
    /// </summary>
    private List<string> _lipSyncParameterIds;

    /// <summary>
    /// モデルが持つ自動まばたき用パラメータIDのハンドル。  モデルとモーションを対応付ける。
    /// </summary>
    private string _modelCurveIdEyeBlink;
    /// <summary>
    /// モデルが持つリップシンク用パラメータIDのハンドル。  モデルとモーションを対応付ける。
    /// </summary>
    private string _modelCurveIdLipSync;
    /// <summary>
    /// モデルが持つ不透明度用パラメータIDのハンドル。  モデルとモーションを対応付ける。
    /// </summary>
    private string _modelCurveIdOpacity;

    /// <summary>
    /// モーションから取得した不透明度
    /// </summary>
    private float _modelOpacity;

    private static CubismMotionPoint LerpPoints(CubismMotionPoint a, CubismMotionPoint b, float t)
    {
        return new()
        {
            Time = a.Time + ((b.Time - a.Time) * t),
            Value = a.Value + ((b.Value - a.Value) * t)
        };
    }

    private static float LinearEvaluate(CubismMotionPoint[] points, int start, float time)
    {
        float t = (time - points[start].Time) / (points[start + 1].Time - points[start].Time);

        if (t < 0.0f)
        {
            t = 0.0f;
        }

        return points[start].Value + ((points[start + 1].Value - points[start].Value) * t);
    }

    private static float BezierEvaluate(CubismMotionPoint[] points, int start, float time)
    {
        float t = (time - points[start].Time) / (points[start + 3].Time - points[start].Time);

        if (t < 0.0f)
        {
            t = 0.0f;
        }

        var p01 = LerpPoints(points[start], points[start + 1], t);
        var p12 = LerpPoints(points[start + 1], points[start + 2], t);
        var p23 = LerpPoints(points[start + 2], points[start + 3], t);

        var p012 = LerpPoints(p01, p12, t);
        var p123 = LerpPoints(p12, p23, t);

        return LerpPoints(p012, p123, t).Value;
    }

    private static float BezierEvaluateCardanoInterpretation(CubismMotionPoint[] points, int start, float time)
    {
        float x = time;
        float x1 = points[start].Time;
        float x2 = points[start + 3].Time;
        float cx1 = points[start + 1].Time;
        float cx2 = points[start + 2].Time;

        float a = x2 - 3.0f * cx2 + 3.0f * cx1 - x1;
        float b = 3.0f * cx2 - 6.0f * cx1 + 3.0f * x1;
        float c = 3.0f * cx1 - 3.0f * x1;
        float d = x1 - x;

        float t = CubismMath.CardanoAlgorithmForBezier(a, b, c, d);

        var p01 = LerpPoints(points[start], points[start + 1], t);
        var p12 = LerpPoints(points[start + 1], points[start + 2], t);
        var p23 = LerpPoints(points[start + 2], points[start + 3], t);

        var p012 = LerpPoints(p01, p12, t);
        var p123 = LerpPoints(p12, p23, t);

        return LerpPoints(p012, p123, t).Value;
    }

    private static float SteppedEvaluate(CubismMotionPoint[] points, int start, float time)
    {
        return points[start].Value;
    }

    private static float InverseSteppedEvaluate(CubismMotionPoint[] points, int start, float time)
    {
        return points[start + 1].Value;
    }

    private static float EvaluateCurve(CubismMotionData motionData, int index, float time)
    {
        // Find segment to evaluate.
        var curve = motionData.Curves[index];

        int target = -1;
        int totalSegmentCount = curve.BaseSegmentIndex + curve.SegmentCount;
        int pointPosition = 0;
        for (int i = curve.BaseSegmentIndex; i < totalSegmentCount; ++i)
        {
            // Get first point of next segment.
            pointPosition = motionData.Segments[i].BasePointIndex
                + (motionData.Segments[i].SegmentType ==
                    CubismMotionSegmentType.Bezier ? 3 : 1);

            // Break if time lies within current segment.
            if (motionData.Points[pointPosition].Time > time)
            {
                target = i;
                break;
            }
        }

        if (target == -1)
        {
            return motionData.Points[pointPosition].Value;
        }

        var segment = motionData.Segments[target];

        return segment.Evaluate(motionData.Points, segment.BasePointIndex, time);
    }

    /// <summary>
    /// インスタンスを作成する。
    /// </summary>
    /// <param name="buffer">motion3.jsonが読み込まれているバッファ</param>
    /// <param name="onFinishedMotionHandler">モーション再生終了時に呼び出されるコールバック関数。NULLの場合、呼び出されない。</param>
    public CubismMotion(string buffer, FinishedMotionCallback? onFinishedMotionHandler = null)
    {
        _loopDurationSeconds = -1.0f;
        IsLoopFadeIn = true;       // ループ時にフェードインが有効かどうかのフラグ
        _modelOpacity = 1.0f;

        using var stream = File.Open(buffer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var obj = JsonSerializer.Deserialize(stream, CubismMotionObjContext.Default.CubismMotionObj)
            ?? throw new Exception("Load Motion error");

        _motionData = new()
        {
            Duration = obj.Meta.Duration,
            Loop = obj.Meta.Loop,
            CurveCount = obj.Meta.CurveCount,
            EventCount = obj.Meta.UserDataCount,
            Curves = new CubismMotionCurve[obj.Meta.CurveCount],
            Segments = new CubismMotionSegment[obj.Meta.TotalSegmentCount],
            Points = new CubismMotionPoint[obj.Meta.TotalPointCount],
            Events = new CubismMotionEvent[obj.Meta.UserDataCount]
        };

        bool areBeziersRestructed = obj.Meta.AreBeziersRestricted;

        if (obj.Meta.FadeInTime != null)
        {
            FadeInSeconds = obj.Meta.FadeInTime < 0.0f ? 1.0f : (float)obj.Meta.FadeInTime;
        }
        else
        {
            FadeInSeconds = 1.0f;
        }

        if (obj.Meta.FadeOutTime != null)
        {
            FadeOutSeconds = obj.Meta.FadeOutTime < 0.0f ? 1.0f : (float)obj.Meta.FadeOutTime;
        }
        else
        {
            FadeOutSeconds = 1.0f;
        }

        int totalPointCount = 0;
        int totalSegmentCount = 0;

        // Curves
        for (int curveCount = 0; curveCount < _motionData.CurveCount; ++curveCount)
        {
            var item = obj.Curves[curveCount];
            string key = item.Target;
            _motionData.Curves[curveCount] = new()
            {
                Id = CubismFramework.CubismIdManager.GetId(item.Id),
                BaseSegmentIndex = totalSegmentCount,
                FadeInTime = item.FadeInTime != null ? (float)item.FadeInTime : -1.0f,
                FadeOutTime = item.FadeOutTime != null ? (float)item.FadeOutTime : -1.0f,
                Type = key switch
                {
                    TargetNameModel => CubismMotionCurveTarget.Model,
                    TargetNameParameter => CubismMotionCurveTarget.Parameter,
                    TargetNamePartOpacity => CubismMotionCurveTarget.PartOpacity,
                    _ => throw new Exception("Error: Unable to get segment type from Curve! The number of \"CurveCount\" may be incorrect!")
                }
            };

            // Segments
            for (int segmentPosition = 0; segmentPosition < item.Segments.Count;)
            {
                if (segmentPosition == 0)
                {
                    _motionData.Segments[totalSegmentCount] = new()
                    {
                        BasePointIndex = totalPointCount
                    };
                    _motionData.Points[totalPointCount] = new()
                    {
                        Time = item.Segments[segmentPosition],
                        Value = item.Segments[segmentPosition + 1]
                    };

                    totalPointCount += 1;
                    segmentPosition += 2;
                }
                else
                {
                    _motionData.Segments[totalSegmentCount] = new()
                    {
                        BasePointIndex = totalPointCount - 1
                    };
                }

                switch ((CubismMotionSegmentType)item.Segments[segmentPosition])
                {
                    case CubismMotionSegmentType.Linear:
                        {
                            _motionData.Segments[totalSegmentCount].SegmentType = CubismMotionSegmentType.Linear;
                            _motionData.Segments[totalSegmentCount].Evaluate = LinearEvaluate;

                            _motionData.Points[totalPointCount] = new()
                            {
                                Time = item.Segments[segmentPosition + 1],
                                Value = item.Segments[segmentPosition + 2]
                            };

                            totalPointCount += 1;
                            segmentPosition += 3;

                            break;
                        }
                    case CubismMotionSegmentType.Bezier:
                        {
                            _motionData.Segments[totalSegmentCount].SegmentType = CubismMotionSegmentType.Bezier;
                            if (areBeziersRestructed)
                            {
                                _motionData.Segments[totalSegmentCount].Evaluate = BezierEvaluate;
                            }
                            else
                            {
                                _motionData.Segments[totalSegmentCount].Evaluate = BezierEvaluateCardanoInterpretation;
                            }

                            _motionData.Points[totalPointCount] = new()
                            {
                                Time = item.Segments[segmentPosition + 1],
                                Value = item.Segments[segmentPosition + 2]
                            };

                            _motionData.Points[totalPointCount + 1] = new()
                            {
                                Time = item.Segments[segmentPosition + 3],
                                Value = item.Segments[segmentPosition + 4]
                            };

                            _motionData.Points[totalPointCount + 2] = new()
                            {
                                Time = item.Segments[segmentPosition + 5],
                                Value = item.Segments[segmentPosition + 6]
                            };

                            totalPointCount += 3;
                            segmentPosition += 7;

                            break;
                        }
                    case CubismMotionSegmentType.Stepped:
                        {
                            _motionData.Segments[totalSegmentCount].SegmentType = CubismMotionSegmentType.Stepped;
                            _motionData.Segments[totalSegmentCount].Evaluate = SteppedEvaluate;

                            _motionData.Points[totalPointCount] = new()
                            {
                                Time = item.Segments[segmentPosition + 1],
                                Value = item.Segments[segmentPosition + 2]
                            };

                            totalPointCount += 1;
                            segmentPosition += 3;

                            break;
                        }
                    case CubismMotionSegmentType.InverseStepped:
                        {
                            _motionData.Segments[totalSegmentCount].SegmentType = CubismMotionSegmentType.InverseStepped;
                            _motionData.Segments[totalSegmentCount].Evaluate = InverseSteppedEvaluate;

                            _motionData.Points[totalPointCount] = new()
                            {
                                Time = item.Segments[segmentPosition + 1],
                                Value = item.Segments[segmentPosition + 2]
                            };

                            totalPointCount += 1;
                            segmentPosition += 3;

                            break;
                        }
                    default:
                        {
                            throw new Exception("CubismMotionSegmentType error");
                        }
                }

                ++_motionData.Curves[curveCount].SegmentCount;
                ++totalSegmentCount;
            }
        }

        for (int userdatacount = 0; userdatacount < obj.Meta.UserDataCount; ++userdatacount)
        {
            _motionData.Events[userdatacount] = new()
            {
                FireTime = obj.UserData[userdatacount].Time,
                Value = obj.UserData[userdatacount].Value
            };
        }

        _loopDurationSeconds = _motionData.Duration;
        OnFinishedMotion = onFinishedMotionHandler;
    }

    /// <summary>
    /// モデルのパラメータ更新を実行する。
    /// </summary>
    /// <param name="model">対象のモデル</param>
    /// <param name="userTimeSeconds">現在の時刻[秒]</param>
    /// <param name="fadeWeight">モーションの重み</param>
    /// <param name="motionQueueEntry">CubismMotionQueueManagerで管理されているモーション</param>
    public override void DoUpdateParameters(CubismModel model, float userTimeSeconds, float fadeWeight, CubismMotionQueueEntry motionQueueEntry)
    {
        _modelCurveIdEyeBlink ??= CubismFramework.CubismIdManager.GetId(EffectNameEyeBlink);

        _modelCurveIdLipSync ??= CubismFramework.CubismIdManager.GetId(EffectNameLipSync);

        _modelCurveIdOpacity ??= CubismFramework.CubismIdManager.GetId(IdNameOpacity);

        float timeOffsetSeconds = userTimeSeconds - motionQueueEntry.StartTime;

        if (timeOffsetSeconds < 0.0f)
        {
            timeOffsetSeconds = 0.0f; // エラー回避
        }

        float lipSyncValue = float.MaxValue;
        float eyeBlinkValue = float.MaxValue;

        //まばたき、リップシンクのうちモーションの適用を検出するためのビット（maxFlagCount個まで
        int MaxTargetSize = 64;
        ulong lipSyncFlags = 0;
        ulong eyeBlinkFlags = 0;

        //瞬き、リップシンクのターゲット数が上限を超えている場合
        if (_eyeBlinkParameterIds.Count > MaxTargetSize)
        {
            CubismLog.Warning($"[Live2D SDK]too many eye blink targets : {_eyeBlinkParameterIds.Count}");
        }
        if (_lipSyncParameterIds.Count > MaxTargetSize)
        {
            CubismLog.Warning($"[Live2D SDK]too many lip sync targets : {_lipSyncParameterIds.Count}");
        }

        float tmpFadeIn = (FadeInSeconds <= 0.0f) ? 1.0f :
           CubismMath.GetEasingSine((userTimeSeconds - motionQueueEntry.FadeInStartTime) / FadeInSeconds);

        float tmpFadeOut = (FadeOutSeconds <= 0.0f || motionQueueEntry.EndTime < 0.0f) ? 1.0f :
           CubismMath.GetEasingSine((motionQueueEntry.EndTime - userTimeSeconds) / FadeOutSeconds);

        float value;
        int c, parameterIndex;

        // 'Repeat' time as necessary.
        float time = timeOffsetSeconds;

        if (IsLoop)
        {
            while (time > _motionData.Duration)
            {
                time -= _motionData.Duration;
            }
        }

        var curves = _motionData.Curves;

        // Evaluate model curves.
        for (c = 0; c < _motionData.CurveCount && curves[c].Type == CubismMotionCurveTarget.Model; ++c)
        {
            // Evaluate curve and call handler.
            value = EvaluateCurve(_motionData, c, time);

            if (curves[c].Id == _modelCurveIdEyeBlink)
            {
                eyeBlinkValue = value;
            }
            else if (curves[c].Id == _modelCurveIdLipSync)
            {
                lipSyncValue = value;
            }
            else if (curves[c].Id == _modelCurveIdOpacity)
            {
                _modelOpacity = value;

                // ------ 不透明度の値が存在すれば反映する ------
                model.SetModelOpacity(GetModelOpacityValue());
            }
        }

        int parameterMotionCurveCount = 0;

        for (; c < _motionData.CurveCount && curves[c].Type == CubismMotionCurveTarget.Parameter; ++c)
        {
            parameterMotionCurveCount++;

            // Find parameter index.
            parameterIndex = model.GetParameterIndex(curves[c].Id);

            // Skip curve evaluation if no value in sink.
            if (parameterIndex == -1)
            {
                continue;
            }

            float sourceValue = model.GetParameterValue(parameterIndex);

            // Evaluate curve and apply value.
            value = EvaluateCurve(_motionData, c, time);

            if (eyeBlinkValue != float.MaxValue)
            {
                for (int i = 0; i < _eyeBlinkParameterIds.Count && i < MaxTargetSize; ++i)
                {
                    if (_eyeBlinkParameterIds[i] == curves[c].Id)
                    {
                        value *= eyeBlinkValue;
                        eyeBlinkFlags |= 1UL << i;
                        break;
                    }
                }
            }

            if (lipSyncValue != float.MaxValue)
            {
                for (int i = 0; i < _lipSyncParameterIds.Count && i < MaxTargetSize; ++i)
                {
                    if (_lipSyncParameterIds[i] == curves[c].Id)
                    {
                        value += lipSyncValue;
                        lipSyncFlags |= 1UL << i;
                        break;
                    }
                }
            }

            float v;
            // パラメータごとのフェード
            if (curves[c].FadeInTime < 0.0f && curves[c].FadeOutTime < 0.0f)
            {
                //モーションのフェードを適用
                v = sourceValue + (value - sourceValue) * fadeWeight;
            }
            else
            {
                // パラメータに対してフェードインかフェードアウトが設定してある場合はそちらを適用
                float fin;
                float fout;

                if (curves[c].FadeInTime < 0.0f)
                {
                    fin = tmpFadeIn;
                }
                else
                {
                    fin = curves[c].FadeInTime == 0.0f ? 1.0f
                            : CubismMath.GetEasingSine((userTimeSeconds - motionQueueEntry.FadeInStartTime) / curves[c].FadeInTime);
                }

                if (curves[c].FadeOutTime < 0.0f)
                {
                    fout = tmpFadeOut;
                }
                else
                {
                    fout = (curves[c].FadeOutTime == 0.0f || motionQueueEntry.EndTime < 0.0f)
                                ? 1.0f
                            : CubismMath.GetEasingSine((motionQueueEntry.EndTime - userTimeSeconds) / curves[c].FadeOutTime);
                }

                float paramWeight = Weight * fin * fout;

                // パラメータごとのフェードを適用
                v = sourceValue + (value - sourceValue) * paramWeight;
            }

            model.SetParameterValue(parameterIndex, v);
        }

        if (eyeBlinkValue != float.MaxValue)
        {
            for (int i = 0; i < _eyeBlinkParameterIds.Count && i < MaxTargetSize; ++i)
            {
                float sourceValue = model.GetParameterValue(_eyeBlinkParameterIds[i]);
                //モーションでの上書きがあった時にはまばたきは適用しない
                if (((eyeBlinkFlags >> i) & 0x01) != 0UL)
                {
                    continue;
                }

                float v = sourceValue + (eyeBlinkValue - sourceValue) * fadeWeight;

                model.SetParameterValue(_eyeBlinkParameterIds[i], v);
            }
        }

        if (lipSyncValue != float.MaxValue)
        {
            for (int i = 0; i < _lipSyncParameterIds.Count && i < MaxTargetSize; ++i)
            {
                float sourceValue = model.GetParameterValue(_lipSyncParameterIds[i]);
                //モーションでの上書きがあった時にはリップシンクは適用しない
                if (((lipSyncFlags >> i) & 0x01) != 0UL)
                {
                    continue;
                }

                float v = sourceValue + (lipSyncValue - sourceValue) * fadeWeight;

                model.SetParameterValue(_lipSyncParameterIds[i], v);
            }
        }

        for (; c < _motionData.CurveCount && curves[c].Type == CubismMotionCurveTarget.PartOpacity; ++c)
        {
            // Find parameter index.
            parameterIndex = model.GetParameterIndex(curves[c].Id);

            // Skip curve evaluation if no value in sink.
            if (parameterIndex == -1)
            {
                continue;
            }

            // Evaluate curve and apply value.
            value = EvaluateCurve(_motionData, c, time);

            model.SetParameterValue(parameterIndex, value);
        }

        if (timeOffsetSeconds >= _motionData.Duration)
        {
            if (IsLoop)
            {
                motionQueueEntry.StartTime = userTimeSeconds; //最初の状態へ
                if (IsLoopFadeIn)
                {
                    //ループ中でループ用フェードインが有効のときは、フェードイン設定し直し
                    motionQueueEntry.FadeInStartTime = userTimeSeconds;
                }
            }
            else
            {
                OnFinishedMotion?.Invoke(model, this);

                motionQueueEntry.Finished = true;
            }
        }
    }

    /// <summary>
    /// モーションの長さを取得する。
    /// </summary>
    /// <returns>モーションの長さ[秒]</returns>
    public override float GetDuration()
    {
        return IsLoop ? -1.0f : _loopDurationSeconds;
    }

    /// <summary>
    /// モーションのループ時の長さを取得する。
    /// </summary>
    /// <returns>モーションのループ時の長さ[秒]</returns>
    public override float GetLoopDuration()
    {
        return _loopDurationSeconds;
    }

    /// <summary>
    /// 自動エフェクトがかかっているパラメータIDリストを設定する。
    /// </summary>
    /// <param name="eyeBlinkParameterIds">自動まばたきがかかっているパラメータIDのリスト</param>
    /// <param name="lipSyncParameterIds">リップシンクがかかっているパラメータIDのリスト</param>
    public void SetEffectIds(List<string> eyeBlinkParameterIds, List<string> lipSyncParameterIds)
    {
        _eyeBlinkParameterIds = eyeBlinkParameterIds;
        _lipSyncParameterIds = lipSyncParameterIds;
    }

    /// <summary>
    /// イベント発火のチェック。
    /// 入力する時間は呼ばれるモーションタイミングを０とした秒数で行う。
    /// </summary>
    /// <param name="beforeCheckTimeSeconds">前回のイベントチェック時間[秒]</param>
    /// <param name="motionTimeSeconds">今回の再生時間[秒]</param>
    /// <returns></returns>
    public override List<string> GetFiredEvent(float beforeCheckTimeSeconds, float motionTimeSeconds)
    {
        FiredEventValues.Clear();
        /// イベントの発火チェック
        for (int u = 0; u < _motionData.EventCount; ++u)
        {
            if ((_motionData.Events[u].FireTime > beforeCheckTimeSeconds) &&
                (_motionData.Events[u].FireTime <= motionTimeSeconds))
            {
                FiredEventValues.Add(_motionData.Events[u].Value);
            }
        }

        return FiredEventValues;

    }

    /// <summary>
    /// 透明度のカーブが存在するかどうかを確認する
    /// </summary>
    /// <returns>true  . キーが存在する
    /// false . キーが存在しない</returns>
    public override bool IsExistModelOpacity()
    {
        for (int i = 0; i < _motionData.CurveCount; i++)
        {
            CubismMotionCurve curve = _motionData.Curves[i];

            if (curve.Type != CubismMotionCurveTarget.Model)
            {
                continue;
            }

            if (curve.Id == IdNameOpacity)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 透明度のカーブのインデックスを返す
    /// </summary>
    /// <returns>透明度のカーブのインデックス</returns>
    public override int GetModelOpacityIndex()
    {
        if (IsExistModelOpacity())
        {
            for (int i = 0; i < _motionData.CurveCount; i++)
            {
                CubismMotionCurve curve = _motionData.Curves[i];

                if (curve.Type != CubismMotionCurveTarget.Model)
                {
                    continue;
                }

                if (curve.Id == IdNameOpacity)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// 透明度のIdを返す
    /// </summary>
    /// <returns>透明度のId</returns>
    public override string? GetModelOpacityId(int index)
    {
        if (index != -1)
        {
            CubismMotionCurve curve = _motionData.Curves[index];

            if (curve.Type == CubismMotionCurveTarget.Model)
            {
                if (curve.Id == IdNameOpacity)
                {
                    return CubismFramework.CubismIdManager.GetId(curve.Id);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 透明度の値を返す
    /// 更新後の値を取るにはUpdateParameters() の後に呼び出す。
    /// </summary>
    /// <returns>モーションの現在時間のOpacityの値</returns>
    public override float GetModelOpacityValue()
    {
        return _modelOpacity;
    }
}
