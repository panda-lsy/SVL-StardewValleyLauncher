using System.Reflection;

// CI 用启动钩子：在应用初始化前检查实际发布程序携带的包体类型。
public static class StartupHook
{
    public static void Initialize()
    {
        var value = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SVL.RuntimeMode")?.Value;
        Environment.Exit(value switch
        {
            "self-contained" => 41,
            "framework-dependent" => 42,
            _ => 43
        });
    }
}
