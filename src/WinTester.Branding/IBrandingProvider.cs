namespace WinTester.Branding;

// Abstraction over the loaded brand.json. Resolved once at startup, injected into every
// view-model, the report generator, and the splash screen; brand.json is never read more than once
// per process lifetime.
public interface IBrandingProvider
{
    BrandProfile Current { get; }

    // Loads brand.json from the given path, or "brand.json" next to the executable if omitted.
    // Falls back silently to BrandProfile's built-in defaults on missing/malformed input.
    void Load(string? brandJsonPath = null);
}
