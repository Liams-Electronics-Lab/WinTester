namespace WinTester.Tests.Gpu;

// Which GravityMark render API to launch with. Only Vulkan is constructed today (per-GPU device-index sweep); the other three exist so a future API-selection UI has somewhere to plug in without a new enum.
public enum GravityMarkApi
{
    Vulkan,
    OpenGl,
    Direct3D11,
    Direct3D12
}

internal static class GravityMarkApiExtensions
{
    // Bare CLI flag per API, sourced from the matching run_fullscreen_*.bat; a standalone switch, unlike FurMark2's named-value --demo argument.
    public static string ToCliFlag(this GravityMarkApi api) =>
        api switch
        {
            GravityMarkApi.Vulkan => "-vk",
            GravityMarkApi.OpenGl => "-gl",
            GravityMarkApi.Direct3D11 => "-d3d11",
            GravityMarkApi.Direct3D12 => "-d3d12",
            _ => "-vk"
        };

    public static string ToDisplayName(this GravityMarkApi api) =>
        api switch
        {
            GravityMarkApi.Vulkan => "Vulkan",
            GravityMarkApi.OpenGl => "OpenGL",
            GravityMarkApi.Direct3D11 => "Direct3D 11",
            GravityMarkApi.Direct3D12 => "Direct3D 12",
            _ => api.ToString()
        };
}
