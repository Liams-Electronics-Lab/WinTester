using System;
using System.IO;
using System.Text.Json;

namespace WinTester.Branding;

public sealed class BrandingService : IBrandingProvider
{
    public BrandProfile Current { get; private set; } = new();

    public void Load(string? brandJsonPath = null)
    {
        var path = brandJsonPath ?? Path.Combine(AppContext.BaseDirectory, "brand.json");

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var loaded = JsonSerializer.Deserialize<BrandProfile>(json, options);
                if (loaded is not null)
                {
                    // A JSON null ("businessDetails": null) must not leave the property null.
                    loaded.BusinessDetails ??= new BusinessDetails();
                    Current = loaded;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a bad/missing brand.json crash startup; fall back to defaults and keep going.
            System.Diagnostics.Debug.WriteLine($"[BrandingService] Failed to load '{path}': {ex.Message}. Using defaults.");
        }

        Current = new BrandProfile();
    }
}
