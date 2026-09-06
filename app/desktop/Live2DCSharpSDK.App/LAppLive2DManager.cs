using Live2DCSharpSDK.Framework;

namespace Live2DCSharpSDK.App;

/// <summary>
/// 管理当前加载的 Cubism 模型实例。
/// </summary>
public class LAppLive2DManager(LAppDelegate lapp) : IDisposable
{
    /// <summary>
    /// 模型实例的容器。
    /// </summary>
    private readonly List<LAppModel> _models = [];

    /// <summary>
    /// 解放当前持有的所有模型。
    /// </summary>
    public void ReleaseAllModel()
    {
        for (int i = 0; i < _models.Count; i++)
        {
            _models[i].Dispose();
        }

        _models.Clear();
    }

    public LAppModel LoadModel(string dir, string name)
    {
        CubismLog.Debug($"[Live2D App]model load: {name}");

        // ModelDir[]に保持したディレクトリ名から
        // model3.jsonのパスを決定する.
        // ディレクトリ名とmodel3.jsonの名前を一致させておくこと.
        if (!dir.EndsWith('\\') && !dir.EndsWith('/'))
        {
            dir = Path.GetFullPath(dir + '/');
        }
        var modelJsonName = Path.GetFullPath($"{dir}{name}");
        if (!File.Exists(modelJsonName))
        {
            modelJsonName = Path.GetFullPath($"{dir}{name}.model3.json");
        }
        if (!File.Exists(modelJsonName))
        {
            dir = Path.GetFullPath(dir + name + '/');
            modelJsonName = Path.GetFullPath($"{dir}{name}.model3.json");
        }
        if (!File.Exists(modelJsonName))
        {
            throw new Exception($"[Live2D]File not found: {modelJsonName}");
        }

        var model = new LAppModel(lapp, dir, modelJsonName);
        _models.Add(model);

        return model;
    }

    public void RemoveModel(LAppModel model)
    {
        _models.Remove(model);
        model.Dispose();
    }

    public void Dispose()
    {
        ReleaseAllModel();
    }
}
