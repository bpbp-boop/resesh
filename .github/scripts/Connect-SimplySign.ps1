[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Username,

    [Parameter(Mandatory)]
    [string] $OtpUri,

    [string] $ExecutablePath = "$env:ProgramFiles\Certum\SimplySign Desktop\SimplySignDesktop.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = [Uri]$OtpUri
if ($uri.Scheme -ne "otpauth") {
    throw "CERTUM_OTP_URI must be an otpauth URI."
}

$query = @{}
foreach ($part in $uri.Query.TrimStart("?") -split "&") {
    $keyValue = $part -split "=", 2
    if ($keyValue.Count -eq 2) {
        $query[[Uri]::UnescapeDataString($keyValue[0])] = [Uri]::UnescapeDataString($keyValue[1])
    }
}

$secret = $query["secret"]
if ([string]::IsNullOrWhiteSpace($secret)) {
    throw "CERTUM_OTP_URI does not contain a TOTP secret."
}

$digits = if ($query["digits"]) { [int]$query["digits"] } else { 6 }
$period = if ($query["period"]) { [int]$query["period"] } else { 30 }
$algorithm = if ($query["algorithm"]) { $query["algorithm"].ToUpperInvariant() } else { "SHA1" }
if ($algorithm -notin @("SHA1", "SHA256", "SHA512")) {
    throw "CERTUM_OTP_URI uses unsupported algorithm '$algorithm'."
}

Add-Type -Language CSharp @"
using System;
using System.Security.Cryptography;

public static class ReseshTotp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static byte[] DecodeBase32(string value)
    {
        value = value.TrimEnd('=').ToUpperInvariant();
        byte[] result = new byte[value.Length * 5 / 8];
        int buffer = 0;
        int bits = 0;
        int index = 0;

        foreach (char character in value)
        {
            int digit = Base32Alphabet.IndexOf(character);
            if (digit < 0)
                throw new ArgumentException("The TOTP secret contains an invalid Base32 character.");

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                result[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return result;
    }

    public static string Generate(string secret, int digits, int period, string algorithm)
    {
        byte[] key = DecodeBase32(secret);
        long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / period;
        byte[] counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        HMAC hmac;
        switch (algorithm)
        {
            case "SHA1":
                hmac = new HMACSHA1(key);
                break;
            case "SHA256":
                hmac = new HMACSHA256(key);
                break;
            case "SHA512":
                hmac = new HMACSHA512(key);
                break;
            default:
                throw new ArgumentException("Unsupported TOTP algorithm.");
        }

        byte[] hash;
        using (hmac)
        {
            hash = hmac.ComputeHash(counterBytes);
        }

        int offset = hash[hash.Length - 1] & 0x0f;
        int binary =
            ((hash[offset] & 0x7f) << 24) |
            ((hash[offset + 1] & 0xff) << 16) |
            ((hash[offset + 2] & 0xff) << 8) |
            (hash[offset + 3] & 0xff);
        int code = binary % (int)Math.Pow(10, digits);
        return code.ToString(new string('0', digits));
    }
}
"@

Add-Type -Language CSharp @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ReseshWindows
{
    public delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    public static List<IntPtr> GetVisibleWindows(uint processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((handle, parameter) =>
        {
            uint ownerProcessId;
            GetWindowThreadProcessId(handle, out ownerProcessId);
            if (ownerProcessId == processId && IsWindowVisible(handle))
                windows.Add(handle);
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
"@

function Get-SimplySignWindows {
    @([ReseshWindows]::GetVisibleWindows([uint32]$process.Id))
}

function Set-WindowFocus([IntPtr] $Handle) {
    foreach ($attempt in 1..10) {
        [ReseshWindows]::SetForegroundWindow($Handle) | Out-Null
        Start-Sleep -Milliseconds 300
        if ([ReseshWindows]::GetForegroundWindow() -eq $Handle) {
            return
        }
    }

    throw "Could not focus the SimplySign login window."
}

function Get-LargestWindow([IntPtr[]] $Windows) {
    $largest = [IntPtr]::Zero
    $largestArea = -1
    foreach ($window in $Windows) {
        $rect = New-Object "ReseshWindows+Rect"
        [ReseshWindows]::GetWindowRect($window, [ref]$rect) | Out-Null
        $area = ($rect.Right - $rect.Left) * ($rect.Bottom - $rect.Top)
        if ($area -gt $largestArea) {
            $largest = $window
            $largestArea = $area
        }
    }

    $largest
}

$registryPath = "HKCU:\Software\Certum\SimplySign"
New-Item -Path $registryPath -Force | Out-Null
$settings = @{
    ShowLoginDialogOnStart = 1
    ShowLoginDialogOnAppRequest = 1
    RememberLastUserName = 1
    Autostart = 0
    UnregisterCertificatesOnDisconnect = 0
    RememberPINinCSP = 1
    ForgetPINinCSPonDisconnect = 1
    LangID = 9
}
foreach ($setting in $settings.GetEnumerator()) {
    New-ItemProperty -Path $registryPath -Name $setting.Key -Value $setting.Value -PropertyType DWord -Force | Out-Null
}

if (-not (Test-Path $ExecutablePath -PathType Leaf)) {
    throw "SimplySign Desktop was not found at '$ExecutablePath'."
}

$process = Start-Process -FilePath $ExecutablePath -PassThru
$loginWindow = [IntPtr]::Zero
foreach ($attempt in 1..30) {
    if ($process.HasExited) {
        throw "SimplySign Desktop exited before it displayed the login window."
    }

    $windows = @(Get-SimplySignWindows)
    if ($windows.Count -gt 0) {
        $candidate = Get-LargestWindow -Windows $windows
        $rect = New-Object "ReseshWindows+Rect"
        [ReseshWindows]::GetWindowRect($candidate, [ref]$rect) | Out-Null
        if (($rect.Right - $rect.Left) -ge 400 -and ($rect.Bottom - $rect.Top) -ge 300) {
            $loginWindow = $candidate
            break
        }
    }
    Start-Sleep -Seconds 1
}
if ($loginWindow -eq [IntPtr]::Zero) {
    throw "SimplySign Desktop did not display the login window."
}

$shell = New-Object -ComObject WScript.Shell

# SimplySign can show an update dialog after the login window opens. Decline it.
Start-Sleep -Seconds 6
$windows = @(Get-SimplySignWindows)
$loginWindow = Get-LargestWindow -Windows $windows
foreach ($popup in @($windows | Where-Object { $_ -ne $loginWindow })) {
    Set-WindowFocus -Handle $popup
    $shell.SendKeys("%n")
    Start-Sleep -Seconds 1
    if (@(Get-SimplySignWindows) -contains $popup) {
        $shell.SendKeys("{ENTER}")
        Start-Sleep -Seconds 1
    }
}

$secondsRemaining = $period - ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % $period)
if ($secondsRemaining -lt 20) {
    Start-Sleep -Seconds ($secondsRemaining + 1)
}
$otp = [ReseshTotp]::Generate($secret, $digits, $period, $algorithm)
Write-Output "::add-mask::$otp"

try {
    Set-WindowFocus -Handle $loginWindow
    Set-Clipboard -Value $Username
    $shell.SendKeys("^a")
    Start-Sleep -Milliseconds 120
    $shell.SendKeys("{DEL}")
    Start-Sleep -Milliseconds 120
    $shell.SendKeys("^v")
    Start-Sleep -Milliseconds 250
    $shell.SendKeys("{TAB}")
    Start-Sleep -Milliseconds 250
    Set-Clipboard -Value $otp
    $shell.SendKeys("^a")
    Start-Sleep -Milliseconds 120
    $shell.SendKeys("{DEL}")
    Start-Sleep -Milliseconds 120
    $shell.SendKeys("^v")
    Start-Sleep -Milliseconds 250
    $shell.SendKeys("{ENTER}")
}
finally {
    Set-Clipboard -Value " "
}

$certificate = $null
foreach ($attempt in 1..36) {
    $certificates = @(
        Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object {
                $_.HasPrivateKey -and
                $_.NotBefore -le [DateTime]::Now -and
                $_.NotAfter -gt [DateTime]::Now
            }
    )

    if ($certificates.Count -gt 1) {
        throw "SimplySign exposed more than one valid code-signing certificate. A certificate selector is required."
    }
    if ($certificates.Count -eq 1) {
        $certificate = $certificates[0]
        break
    }

    Start-Sleep -Seconds 5
}
if ($null -eq $certificate) {
    throw "SimplySign did not expose a valid code-signing certificate."
}

"CERTUM_CERT_THUMBPRINT=$($certificate.Thumbprint)" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
Write-Host "SimplySign exposed code-signing certificate '$($certificate.Subject)' with thumbprint '$($certificate.Thumbprint)'."
