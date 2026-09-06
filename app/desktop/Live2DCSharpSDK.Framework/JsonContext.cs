using System.Text.Json.Serialization;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.Framework.Physics;

namespace Live2DCSharpSDK.Framework;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ModelSettingObj))]
public partial class ModelSettingObjContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(CubismMotionObj))]
public partial class CubismMotionObjContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(CubismPhysicsObj))]
public partial class CubismPhysicsObjContext : JsonSerializerContext
{
}


