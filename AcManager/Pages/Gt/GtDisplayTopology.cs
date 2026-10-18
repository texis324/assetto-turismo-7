using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AcManager.Pages.Gt {
    // CCD snapshots preserve adapter identities, timing and the original primary display.
    // AC/DXGI can reset to the persisted desktop while initializing its renderer.
    // Persist the session layout, then restore and persist the captured layout on exit.
    internal static class GtDisplayTopology {
        [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint Lo; public int Hi; }
        [StructLayout(LayoutKind.Sequential)] public struct Source { public Luid Adapter; public uint Id, Mode, Flags; }
        [StructLayout(LayoutKind.Sequential)] public struct Rational { public uint N, D; }
        [StructLayout(LayoutKind.Sequential)] public struct Target {
            public Luid Adapter;
            public uint Id, Mode, Technology, Rotation, Scaling;
            public Rational Refresh;
            public uint ScanLine;
            public int Available;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] public struct DisplayPath { public Source Source; public Target Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential, Size = 64)] public struct Mode {
            public uint Type, Id;
            public Luid Adapter;
            // Keep all 48 bytes of the native union, including target timing, for exact restoration.
            public ulong A, B, C, D, E, F;
            public static Mode Desktop(Luid adapter, uint id, int x) {
                return new Mode { Type = 1, Id = id, Adapter = adapter,
                    A = 1920UL | (1080UL << 32), B = 4UL | ((ulong)(uint)x << 32) };
            }
        }
        public sealed class Snapshot { public DisplayPath[] Paths; public Mode[] Modes; }
        public sealed class Layout {
            public bool Triple;
            public Rectangle Bounds;
            public int ActiveScreens;
            public string Reason;
        }

        // Read the current desktop at launch time. Connected but disabled monitors and
        // cloned targets must not activate three-screen rendering or change Windows.
        public static Layout Automatic() => SelectAutomatic(Capture());

        internal static Layout SelectAutomatic(Snapshot snapshot) {
            if (snapshot?.Paths == null || snapshot.Modes == null)
                throw new InvalidOperationException("現在の画面構成を読み取れませんでした。");
            var sources = new Dictionary<string, Rectangle>();
            foreach (var path in snapshot.Paths.Where(x => (x.Flags & 1) != 0)) {
                if (path.Target.Available == 0 || path.Source.Mode >= snapshot.Modes.Length)
                    throw new InvalidOperationException("有効な画面のモード情報がありません。画面構成が落ち着いてから起動してください。");
                var mode = snapshot.Modes[path.Source.Mode];
                if (mode.Type != 1 || mode.Id != path.Source.Id
                        || mode.Adapter.Hi != path.Source.Adapter.Hi || mode.Adapter.Lo != path.Source.Adapter.Lo)
                    throw new InvalidOperationException("有効な画面とモード情報が一致しません。画面構成が落ち着いてから起動してください。");
                var width = (uint)mode.A;
                var height = (uint)(mode.A >> 32);
                var x = (int)(uint)(mode.B >> 32);
                var y = (int)(uint)mode.C;
                if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
                        || (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
                    throw new InvalidOperationException("有効な画面の解像度または配置が不正です。");
                var bounds = new Rectangle(x, y, (int)width, (int)height);
                var identity = path.Source.Adapter.Hi + ":" + path.Source.Adapter.Lo + ":" + path.Source.Id;
                Rectangle existing;
                if (sources.TryGetValue(identity, out existing) && existing != bounds)
                    throw new InvalidOperationException("複製画面のモード情報が一致しません。");
                sources[identity] = bounds;
            }
            var screens = sources.Values.OrderBy(x => x.X).ThenBy(x => x.Y).ToArray();
            if (screens.Length == 0) throw new InvalidOperationException("有効な画面が見つかりません。");
            var primary = screens.Where(x => x.X == 0 && x.Y == 0).ToArray();
            if (primary.Length != 1) throw new InvalidOperationException("プライマリ画面を特定できません。Windows の画面配置を確認してください。");
            var result = new Layout {
                Bounds = primary[0], ActiveScreens = screens.Length,
                Reason = screens.Length == 1 ? "Windows が1画面のため、1画面で起動します。"
                        : "Windows が横並びの3画面構成ではないため、プライマリの1画面で起動します。"
            };
            if (screens.Length != 3) return result;
            var first = screens[0];
            if (screens.Any(x => x.Width != first.Width || x.Height != first.Height || x.Y != first.Y)
                    || screens[1].X != first.Right || screens[2].X != screens[1].Right) {
                result.Reason = "3画面の解像度・高さ・横並びの配置が揃っていないため、プライマリの1画面で起動します。";
                return result;
            }
            var totalWidth = (long)screens[2].Right - first.X;
            if (totalWidth > int.MaxValue) throw new InvalidOperationException("3画面を合わせた解像度が大きすぎます。");
            result.Triple = true;
            result.Bounds = new Rectangle(first.X, first.Y, (int)totalWidth, first.Height);
            result.Reason = "Windows が横並びの3画面のため、現在の配置で3画面起動します。";
            return result;
        }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray,
                ref uint modes, [Out] Mode[] modeArray, IntPtr topology);
        [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint paths, [In] DisplayPath[] pathArray,
                uint modes, [In] Mode[] modeArray, uint flags);

        public static Snapshot Capture(uint flags = 2) {
            for (var attempt = 0; attempt < 4; attempt++) {
                uint pc, mc;
                Check(GetDisplayConfigBufferSizes(flags, out pc, out mc));
                var paths = new DisplayPath[pc];
                var modes = new Mode[mc];
                var result = QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (result == 122) continue;
                Check(result);
                return new Snapshot { Paths = paths.Take((int)pc).ToArray(), Modes = modes.Take((int)mc).ToArray() };
            }
            throw new InvalidOperationException("画面構成が変更中です。落ち着いてから起動してください。");
        }

        public static void Apply(Snapshot state, bool allowChanges = false) {
            if (state?.Paths == null || state.Paths.Length == 0 || state.Modes == null) throw new InvalidOperationException("画面構成がありません。");
            var flags = 0x20u | (allowChanges ? 0x400u : 0);
            Check(SetDisplayConfig((uint)state.Paths.Length, state.Paths, (uint)state.Modes.Length, state.Modes, flags | 0x40));
            Check(SetDisplayConfig((uint)state.Paths.Length, state.Paths, (uint)state.Modes.Length, state.Modes, flags | 0x80 | 0x200));
        }

        public static Snapshot Triple() {
            // The same physically verified left/centre/right panels as the existing sim profile.
            return Build(new[] { "GC46005744RR", "GFAA231648MS", "GFAA253828AR" }, 1, "三画面用のモニター");
        }

        // [fork] 4画面(観戦): GigaCrysta を primary (0,0) に、3画面を 1920..7680 に並べる（Set-DisplayProfile.ps1 の 'all' と同じ）
        public static Snapshot Spectator() {
            return Build(new[] { "126872", "GC46005744RR", "GFAA231648MS", "GFAA253828AR" }, 0, "4画面用のモニター");
        }

        private static Snapshot Build(string[] serials, int primaryIndex, string what) {
            var ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            using (var search = new ManagementObjectSearcher("root\\wmi", "SELECT SerialNumberID, InstanceName FROM WmiMonitorID"))
            using (var monitors = search.Get()) {
                foreach (ManagementObject monitor in monitors) {
                    using (monitor) {
                        var raw = monitor["SerialNumberID"] as ushort[];
                        var serial = raw == null ? "" : new string(raw.Where(x => x != 0).Select(x => (char)x).ToArray());
                        var match = Regex.Match(monitor["InstanceName"] as string ?? "", @"UID(\d+)");
                        uint id;
                        if (match.Success && uint.TryParse(match.Groups[1].Value, out id)) ids[serial] = id;
                    }
                }
            }
            if (serials.Any(x => !ids.ContainsKey(x))) throw new InvalidOperationException(what + "が見つかりません。" + serials.Length + "台の電源と接続を確認してください。");
            var available = Capture(1);
            var paths = new DisplayPath[serials.Length];
            var modes = new Mode[serials.Length];
            var sourceIds = new Dictionary<string, uint>();
            for (var i = 0; i < serials.Length; i++) {
                var candidates = available.Paths.Where(x => x.Target.Id == ids[serials[i]] && x.Target.Available != 0).ToArray();
                if (candidates.Length == 0) throw new InvalidOperationException(what + "が使用できません。");
                var path = candidates[0];
                var adapter = path.Source.Adapter;
                var key = adapter.Hi + ":" + adapter.Lo;
                uint sourceId;
                sourceIds.TryGetValue(key, out sourceId);
                sourceIds[key] = sourceId + 1;
                path.Source = new Source { Adapter = adapter, Id = sourceId, Mode = (uint)i };
                path.Target.Mode = uint.MaxValue;
                // [fork] ★リフレッシュレートはここで指定しない（OS 任せ = GigaCrysta は 59.934Hz CVT-RB 138.5MHz）。
                // 2026-09-19 に全台 60.000Hz(148.5MHz) へ揃えたら、GigaCrysta で画面の右 1/3 が別フレームになる縦のティアリングが出た
                // （CM7 の放置スライドでも発生 = アプリより下の層）。指定するなら Refresh と一緒に ScanLine=1 が必須（0 のままだと 87）。
                if (path.Target.Rotation == 0) path.Target.Rotation = 1;
                if (path.Target.Scaling == 0) path.Target.Scaling = 1;
                path.Flags = 1;
                paths[i] = path;
                modes[i] = Mode.Desktop(adapter, sourceId, (i - primaryIndex) * 1920);
            }
            return new Snapshot { Paths = paths, Modes = modes };
        }

        public static void VerifyTriple() { VerifyLayout(new[] { -1920, 0, 1920 }, "三画面"); }
        public static void VerifySpectator() { VerifyLayout(new[] { 0, 1920, 3840, 5760 }, "4画面"); }

        private static void VerifyLayout(int[] expected, string what) {
            var actual = Capture();
            var xPositions = new List<int>();
            if (actual.Paths.Length != expected.Length) throw new InvalidOperationException(what + "の有効化を確認できませんでした。");
            foreach (var path in actual.Paths) {
                if (path.Source.Mode >= actual.Modes.Length) throw new InvalidOperationException("画面のモード情報がありません。");
                var mode = actual.Modes[path.Source.Mode];
                if (mode.Type != 1 || (uint)mode.A != 1920 || (uint)(mode.A >> 32) != 1080 || (int)(uint)mode.C != 0)
                    throw new InvalidOperationException(what + "の解像度または配置が一致しません。");
                xPositions.Add((int)(mode.B >> 32));
            }
            if (!xPositions.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)))
                throw new InvalidOperationException(what + "の左右配置が一致しません。");
        }

        private static void Check(int error) { if (error != 0) throw new Win32Exception(error, "画面構成を変更できませんでした (" + error + ")。"); }
    }
}
