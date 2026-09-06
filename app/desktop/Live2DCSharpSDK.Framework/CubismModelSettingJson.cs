namespace Live2DCSharpSDK.Framework;

public static class CubismModelSettingJson
{
    public const string EyeBlink = "EyeBlink";
    public const string LipSync = "LipSync";

    public static bool GetLayoutMap(this ModelSettingObj obj, Dictionary<string, float> outLayoutMap)
    {
        var node = obj.Layout;
        if (node == null)
            return false;

        var ret = false;
        foreach (var item in node)
        {
            if (outLayoutMap.ContainsKey(item.Key))
            {
                outLayoutMap[item.Key] = item.Value;
            }
            else
            {
                outLayoutMap.Add(item.Key, item.Value);
            }
            ret = true;
        }
        return ret;
    }
}
