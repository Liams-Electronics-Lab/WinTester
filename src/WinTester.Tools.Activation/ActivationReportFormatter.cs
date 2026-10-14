using System.Text;
using WinTester.Core.Activation;

namespace WinTester.Tools.Activation;

// Formats an ActivationSnapshot as the plain-text block for the Windows Activation tool window, matching
// get_activation.ps1's on-screen section layout.
public static class ActivationReportFormatter
{
    public static string Format(ActivationSnapshot s)
    {
        var sb = new StringBuilder();

        sb.AppendLine("OPERATING SYSTEM");
        sb.AppendLine("--------------------------------------");
        sb.AppendLine($"Name            : {s.OsName}");
        sb.AppendLine($"Version         : {s.OsVersion}");
        sb.AppendLine($"Build Number    : {s.BuildNumber}");
        sb.AppendLine();

        sb.AppendLine("ACTIVATION STATUS");
        sb.AppendLine("--------------------------------------");
        sb.AppendLine($"Status          : {s.LicenseStatus}");
        sb.AppendLine($"Activation Method : {s.ActivationMethod}");
        if (!string.IsNullOrEmpty(s.KmsServer))
            sb.AppendLine($"KMS Server      : {s.KmsServer} : {s.KmsPort}");
        sb.AppendLine();

        sb.AppendLine("PRODUCT KEY");
        sb.AppendLine("--------------------------------------");
        if (!string.IsNullOrEmpty(s.KeySource))
            sb.AppendLine($"Source          : {s.KeySource}");
        sb.AppendLine($"Key             : {s.ProductKey}");
        sb.AppendLine();

        sb.AppendLine("ACTIVATION EXPIRY");
        sb.AppendLine("--------------------------------------");
        sb.AppendLine(s.ExpiryText);

        if (!string.IsNullOrEmpty(s.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine($"NOTE: {s.ErrorMessage}");
        }

        return sb.ToString();
    }
}
