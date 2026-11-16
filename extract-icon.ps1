param(
    [string]$ApplicationPath = (Join-Path $env:LOCALAPPDATA 'Programs\antigravity\Antigravity.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
public static class AntigravityIconResource {
    private delegate bool NameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, NameCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
    private static byte[] Read(IntPtr module, IntPtr name, int type) {
        IntPtr info = FindResource(module, name, new IntPtr(type));
        if (info == IntPtr.Zero) throw new InvalidOperationException("Icon resource not found.");
        byte[] bytes = new byte[checked((int)SizeofResource(module, info))];
        IntPtr data = LockResource(LoadResource(module, info));
        if (data == IntPtr.Zero) throw new InvalidOperationException("Icon resource could not be loaded.");
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }
    public static int Export(string source, string output) {
        // Load only resource data; never execute the target program.
        IntPtr module = LoadLibraryEx(source, IntPtr.Zero, 0x22);
        if (module == IntPtr.Zero) throw new InvalidOperationException("Could not open Antigravity icon resources.");
        try {
            byte[] group = null;
            NameCallback callback = delegate(IntPtr m, IntPtr t, IntPtr n, IntPtr p) { group = Read(m, n, 14); return false; };
            EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (group == null || group.Length < 6) throw new InvalidOperationException("Antigravity has no icon group.");
            int count = BitConverter.ToUInt16(group, 4);
            var images = new List<byte[]>();
            for (int i=0; i<count; i++) images.Add(Read(module, new IntPtr(BitConverter.ToUInt16(group, 6+i*14+12)), 3));
            using (var stream = File.Create(output)) using (var writer = new BinaryWriter(stream)) {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count);
                int offset = 6+count*16;
                for (int i=0; i<count; i++) {
                    writer.Write(group, 6+i*14, 8);
                    writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
            }
            return count;
        } finally { FreeLibrary(module); }
    }
}
'@
$iconOutput = Join-Path $PSScriptRoot 'antigravity.ico'
$imageCount = [AntigravityIconResource]::Export($ApplicationPath, $iconOutput)
[PSCustomObject]@{ Icon = $iconOutput; Resolutions = $imageCount; Bytes = (Get-Item -LiteralPath $iconOutput).Length }
