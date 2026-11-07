function Resolve-DotNetSdk([string]$ProjectRoot, [string]$ExplicitPath) {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($ExplicitPath) {
        $candidates.Add($ExplicitPath)
    }
    else {
        $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $candidates.Add($command.Source) }
        if ($env:DOTNET_HOST_PATH) { $candidates.Add($env:DOTNET_HOST_PATH) }
        if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
        $candidates.Add((Join-Path $ProjectRoot '.dotnet\dotnet.exe'))
        $candidates.Add((Join-Path $ProjectRoot 'obj\audit-tools\dotnet\dotnet.exe'))
        if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
        if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }
        if ($env:USERPROFILE) { $candidates.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $resolved = (Resolve-Path -LiteralPath $candidate).ProviderPath
        $sdks = & $resolved --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 })) {
            return $resolved
        }
    }
    throw '.NET SDK 10 or newer was not found. Install the SDK, or pass -DotNetPath "C:\path\to\dotnet.exe". A runtime alone cannot build this project.'
}

function Resolve-VisualCppToolchain([string]$ProjectRoot, [string]$ExplicitPath) {
    if ($ExplicitPath) {
        $vcvars = Join-Path $ExplicitPath 'VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path -LiteralPath $vcvars -PathType Leaf) { return $vcvars }
        throw "Visual C++ x64 tools were not found under: $ExplicitPath"
    }
    if ($env:VSINSTALLDIR) {
        $vcvars = Join-Path $env:VSINSTALLDIR 'VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path -LiteralPath $vcvars -PathType Leaf) { return $vcvars }
    }
    $vswhere = Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $vswherePath = if ($vswhere) { $vswhere.Source } else {
        Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if (Test-Path -LiteralPath $vswherePath -PathType Leaf) {
        $installation = & $vswherePath -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) {
            $vcvars = Join-Path ($installation | Select-Object -First 1) 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path -LiteralPath $vcvars -PathType Leaf) { return $vcvars }
        }
    }
    $roots = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio'),
        (Join-Path ([IO.Path]::GetPathRoot($ProjectRoot)) 'Development\MicrosoftVisualStudio')
    )
    foreach ($root in ($roots | Select-Object -Unique)) {
        foreach ($year in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
            foreach ($edition in (Get-ChildItem -LiteralPath $year.FullName -Directory -ErrorAction SilentlyContinue)) {
                $vcvars = Join-Path $edition.FullName 'VC\Auxiliary\Build\vcvars64.bat'
                if (Test-Path -LiteralPath $vcvars -PathType Leaf) { return $vcvars }
            }
        }
    }
    throw 'Visual C++ x64 tools were not found. Install Desktop development with C++, or pass -VisualStudioPath "C:\path\to\BuildTools".'
}

function Resolve-WindowsBuildSdk([string]$ProjectRoot, [string]$ExplicitPath) {
    $roots = if ($ExplicitPath) { @($ExplicitPath) } else {
        @(
            $env:WindowsSdkDir,
            (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10,
            (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10,
            (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'),
            (Join-Path ([IO.Path]::GetPathRoot($ProjectRoot)) 'Windows Kits\10')
        )
    }
    foreach ($root in ($roots | Where-Object { $_ } | Select-Object -Unique)) {
        foreach ($version in (Get-ChildItem -LiteralPath (Join-Path $root 'Include') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } | Sort-Object { [Version]$_.Name } -Descending)) {
            $sdkVersion = $version.Name
            $bin = Join-Path $root "bin\$sdkVersion\x64"
            $um = Join-Path $root "Lib\$sdkVersion\um\x64"
            $ucrt = Join-Path $root "Lib\$sdkVersion\ucrt\x64"
            if ((Test-Path -LiteralPath (Join-Path $bin 'rc.exe') -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $um 'kernel32.lib') -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $ucrt 'libucrt.lib') -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $version.FullName 'um\windows.h') -PathType Leaf)) {
                return [pscustomobject]@{ Bin = $bin; Include = $version.FullName; UmLib = $um; UcrtLib = $ucrt }
            }
        }
    }
    throw 'Windows SDK build tools were not found. Install the Windows SDK, or pass -WindowsSdkPath "C:\path\to\Windows Kits\10".'
}

function Initialize-PayloadCompression {
    if ('TCPTunnelPayloadCompression' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

public static class TCPTunnelPayloadCompression
{
    private const int InsufficientBuffer = 122;

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr allocation, out IntPtr compressor);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Compress(IntPtr compressor, byte[] input, UIntPtr length,
        [Out] byte[] output, UIntPtr capacity, out UIntPtr written);

    [DllImport("cabinet.dll")]
    private static extern bool CloseCompressor(IntPtr compressor);

    public static void Pack(string source, string destination, uint algorithm, int maximumBytes)
    {
        byte[] input = File.ReadAllBytes(source);
        if (input.Length == 0 || input.Length > maximumBytes)
            throw new InvalidDataException("Unsupported payload size: " + source);

        IntPtr compressor;
        if (!CreateCompressor(algorithm, IntPtr.Zero, out compressor))
            throw new Win32Exception();
        try
        {
            UIntPtr length = new UIntPtr((uint)input.Length);
            UIntPtr capacity;
            if (Compress(compressor, input, length, null, UIntPtr.Zero, out capacity) ||
                Marshal.GetLastWin32Error() != InsufficientBuffer)
                throw new Win32Exception();

            byte[] output = new byte[checked((int)capacity.ToUInt64())];
            UIntPtr written;
            if (!Compress(compressor, input, length, output, capacity, out written))
                throw new Win32Exception();

            using (SHA256 sha = SHA256.Create())
            using (BinaryWriter writer = new BinaryWriter(File.Create(destination)))
            {
                writer.Write(input.Length);
                writer.Write(sha.ComputeHash(input));
                writer.Write(output, 0, checked((int)written.ToUInt64()));
            }
        }
        finally
        {
            CloseCompressor(compressor);
        }
    }
}
'@
}

function ConvertTo-RcPath([string]$Path) {
    return ([System.IO.Path]::GetFullPath($Path)).Replace("\", "\\")
}

function Build-PayloadBootstrapper(
    [string]$bootstrapperDirectory, [string]$bootstrapperSource, [string]$resourceFile,
    [string]$stagedExecutable, [string]$vcvars, $windowsBuildSdk, [string]$defines
) {
    $resourceObject = Join-Path $bootstrapperDirectory "TCPTunnel.Resources.res"
    $objectFile = Join-Path $bootstrapperDirectory "TCPTunnel.Bootstrapper.obj"
    $nativeBuildScript = Join-Path $bootstrapperDirectory "BuildBootstrapper.cmd"
    $nativeCommands = @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b %errorlevel%
set "PATH=$($windowsBuildSdk.Bin);%PATH%"
set "INCLUDE=$($windowsBuildSdk.Include)\ucrt;$($windowsBuildSdk.Include)\shared;$($windowsBuildSdk.Include)\um;%INCLUDE%"
set "LIB=$($windowsBuildSdk.UmLib);$($windowsBuildSdk.UcrtLib);%LIB%"
rc.exe /nologo /fo"$resourceObject" "$resourceFile"
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /O1 /MT /guard:cf /W4 /WX /utf-8 /DUNICODE /D_UNICODE $defines /Fo"$objectFile" /Fe"$stagedExecutable" "$bootstrapperSource" "$resourceObject" /link /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf /CETCOMPAT shell32.lib advapi32.lib user32.lib cabinet.lib bcrypt.lib
exit /b %errorlevel%
"@
    [System.IO.File]::WriteAllText($nativeBuildScript, $nativeCommands, [System.Text.Encoding]::ASCII)

    & $nativeBuildScript
    if ($LASTEXITCODE -ne 0) {
        throw "Native bootstrapper build failed with exit code $LASTEXITCODE."
    }
}
