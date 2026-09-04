using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Chengshi.Core;

namespace Chengshi.Engine;

public static class ProcessIdentityFactory
{
    public static ProcessIdentity FromPid(int pid, int parentPid = 0, string? hintName = null)
    {
        string fileName = NormalizeFileName(hintName);
        string? imagePath = null;
        string? publisher = null;
        string? packageFamilyName = null;
        var resolvedParent = parentPid;
        var sessionId = 0;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (resolvedParent == 0)
            {
                resolvedParent = TryGetParentPid(process);
            }

            sessionId = process.SessionId;

            try
            {
                imagePath = process.MainModule?.FileName;
            }
            catch (Exception)
            {
                // 受保护进程拿不到模块路径。
            }

            packageFamilyName = TryGetPackageFamilyName(process.Handle);
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                fileName = Path.GetFileName(imagePath);
                publisher = TryPublisher(imagePath);
            }
            else if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = NormalizeFileName(process.ProcessName);
            }
        }
        catch (Exception)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"pid-{pid}.exe";
            }
        }

        return new ProcessIdentity(pid, resolvedParent, fileName, imagePath, publisher, packageFamilyName, sessionId);
    }

    /// <summary>
    /// UWP/商店应用的身份是包家族名（如 Microsoft.WindowsCalculator_8wekyb3d8bbwe），
    /// 按 exe 名拦它们会误伤；取不到（Win32 程序）返回 null，按其他规则走。
    /// </summary>
    private static string? TryGetPackageFamilyName(IntPtr processHandle)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint length = 0;
            var rc = GetPackageFullName(processHandle, ref length, IntPtr.Zero);
            if (rc != 122 /* ERROR_INSUFFICIENT_BUFFER */ || length == 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
            rc = GetPackageFullName(processHandle, ref length, buffer);
            if (rc != 0)
            {
                return null;
            }

            var fullName = Marshal.PtrToStringUni(buffer, (int)length - 1);
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return null;
            }

            // 完整名形如 Name_Version_Arch__ResourceId_PublisherHash，家族名 = Name_PublisherHash。
            var parts = fullName.Split('_');
            return parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[^1])
                ? $"{parts[0]}_{parts[^1]}"
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int GetPackageFullName(
        IntPtr hProcess,
        ref uint packageFullNameLength,
        IntPtr packageFullName);

    public static string NormalizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var file = Path.GetFileName(name.Trim().Trim('"'));
        var slash = file.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            file = file[(slash + 1)..];
        }

        if (file.Length == 0)
        {
            return string.Empty;
        }

        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
    }

    private static string? TryPublisher(string imagePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(imagePath));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryGetParentPid(Process process)
    {
        try
        {
            return ParentProcessUtilities.GetParentProcessId(process.Id);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
