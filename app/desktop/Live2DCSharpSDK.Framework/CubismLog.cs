namespace Live2DCSharpSDK.Framework;

public static class CubismLog
{
    private static void CubismLogPrintln(LogLevel level, string head, string fmt, params object?[] args)
    {
        string data = $"[CSM] {head} {string.Format(fmt, args)}";
        if (level < CubismFramework.GetLoggingLevel())
            return;

        CubismFramework.CoreLogFunction(data);
    }

    public static void Debug(string fmt, params object?[] args)
    {
        CubismLogPrintln(LogLevel.Debug, "[D]", fmt, args);
    }

    public static void Info(string fmt, params object?[] args)
    {
        CubismLogPrintln(LogLevel.Info, "[I]", fmt, args);
    }

    public static void Warning(string fmt, params object?[] args)
    {
        CubismLogPrintln(LogLevel.Warning, "[W]", fmt, args);
    }

    public static void Error(string fmt, params object?[] args)
    {
        CubismLogPrintln(LogLevel.Error, "[E]", fmt, args);
        throw new Exception(string.Format(fmt, args));
    }
}
