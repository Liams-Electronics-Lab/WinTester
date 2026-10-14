using System;

namespace WinTester.App.Audio;

// Minimal in-place iterative radix-2 Cooley-Tukey FFT. Only used to turn a windowed block of samples into a
// magnitude spectrum for the visualizer's bar mode — not a general-purpose DSP utility — so it's a private
// ~30-line implementation rather than pulling in MathNet.Numerics for one call site.
public static class SimpleFft
{
    // real/imag length must be a power of two. Transforms in place.
    public static void Transform(float[] real, float[] imag)
    {
        var n = real.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j |= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Iterative Cooley-Tukey butterfly passes.
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wr = (float)Math.Cos(ang);
            var wi = (float)Math.Sin(ang);

            for (var i = 0; i < n; i += len)
            {
                float curWr = 1, curWi = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = real[i + k];
                    var ui = imag[i + k];
                    var vr = real[i + k + len / 2] * curWr - imag[i + k + len / 2] * curWi;
                    var vi = real[i + k + len / 2] * curWi + imag[i + k + len / 2] * curWr;

                    real[i + k] = ur + vr;
                    imag[i + k] = ui + vi;
                    real[i + k + len / 2] = ur - vr;
                    imag[i + k + len / 2] = ui - vi;

                    var nextWr = curWr * wr - curWi * wi;
                    var nextWi = curWr * wi + curWi * wr;
                    curWr = nextWr;
                    curWi = nextWi;
                }
            }
        }
    }
}
