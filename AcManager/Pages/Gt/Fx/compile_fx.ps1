# compile_fx.ps1 — [fork] ワールドマップ用シェーダーを ps_3_0 にコンパイルして Data\Static\gt7\fx へ置く（fxc 不要・Windows 標準の d3dcompiler_47.dll）
#   pwsh -File compile_fx.ps1 [-Out D:\actools-build\run\Data\Static\gt7\fx]
param([string]$Out = 'D:\actools-build\run\Data\Static\gt7\fx')
$ErrorActionPreference = 'Stop'
if (-not ('FxCompile.D3D' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
namespace FxCompile {
  [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface ID3DBlob { [PreserveSig] IntPtr GetBufferPointer(); [PreserveSig] IntPtr GetBufferSize(); }
  public static class D3D {
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    static extern int D3DCompile(byte[] src, IntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint f1, uint f2, out ID3DBlob code, out ID3DBlob err);
    static byte[] Bytes(ID3DBlob b) { if (b == null) return new byte[0]; var n = (int)b.GetBufferSize(); var a = new byte[n]; Marshal.Copy(b.GetBufferPointer(), a, 0, n); return a; }
    public static byte[] Compile(string src, string name, string entry, string target, out string errors) {
      var bytes = System.Text.Encoding.UTF8.GetBytes(src);
      ID3DBlob code, err;
      var hr = D3DCompile(bytes, (IntPtr)bytes.Length, name, IntPtr.Zero, IntPtr.Zero, entry, target, 1u << 15 /*OPTIMIZATION_LEVEL3*/, 0, out code, out err);
      errors = System.Text.Encoding.ASCII.GetString(Bytes(err));
      return hr < 0 ? null : Bytes(code);
    }
  }
}
"@
}
New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($f in Get-ChildItem $PSScriptRoot -Filter *.hlsl) {
    $src = [IO.File]::ReadAllText($f.FullName)
    $err = $null
    $code = [FxCompile.D3D]::Compile($src, $f.Name, 'main', 'ps_3_0', [ref]$err)
    if ($err) { Write-Host $err }
    if ($code -eq $null) { throw "compile failed: $($f.Name)" }
    $dst = Join-Path $Out ([IO.Path]::ChangeExtension($f.Name, '.ps'))
    [IO.File]::WriteAllBytes($dst, $code)
    "compiled: $dst ($($code.Length) bytes)"
}
