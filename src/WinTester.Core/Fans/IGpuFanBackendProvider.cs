namespace WinTester.Core.Fans;

// Optional capability on a fan backend that can hand out a second, GPU-only view of itself.
//
// A machine can have both a laptop EC backend for chassis fans and a GPU backend for a discrete GPU's
// fan. FanBackendSelector picks exactly one backend, so on a Gigabyte/ASUS/Dell laptop the vendor
// backend wins and the generic LibreHardwareMonitor backend — the only thing that sees GPU fans — would
// otherwise be dropped entirely, silently taking the discrete GPU's fan with it.
//
// The generic LHM backend implements this so the selector can keep a GPU-scoped slice of it alongside
// whichever vendor backend handles the chassis fans (see CompositeFanBackend). Vendor backends don't
// implement it: none of them can see a GPU fan.
public interface IGpuFanBackendProvider
{
    // Returns a backend restricted to GPU fans only, or null when this machine exposes none (no discrete
    // GPU, or a GPU whose driver reports no fan sensor — common on laptop iGPUs and Intel Arc).
    //
    // The returned backend shares the caller's underlying hardware handles rather than opening its own,
    // so it must not be used after the provider is disposed. Must never throw.
    IFanControlService? CreateGpuFanBackend();
}
