namespace WinTester.Tests.Gpu;

// Which FurMark2 `--demo` to run; GpuStressTestModule falls back to GL-only when Vulkan fails on the machine.
public enum FurmarkEngine
{
    OpenGl,
    Vulkan
}

internal static class FurmarkEngineExtensions
{
    public static string ToDemoName(this FurmarkEngine engine) =>
        engine switch
        {
            FurmarkEngine.OpenGl => "furmark-gl",
            FurmarkEngine.Vulkan => "furmark-vk",
            _ => "furmark-gl"
        };

    public static string ToDisplayName(this FurmarkEngine engine) =>
        engine switch
        {
            FurmarkEngine.OpenGl => "OpenGL",
            FurmarkEngine.Vulkan => "Vulkan",
            _ => engine.ToString()
        };
}
