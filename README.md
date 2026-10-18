# Assetto Turismo 7

A GT-style front end for Assetto Corsa — a fork of [Content Manager](https://github.com/gro-ove/actools) by gro-ove (x4fab).

It replaces the usual menus with a console-style flow: world map → region map → course entry → loading screen → race, fully usable with a gamepad or a wheel D-pad.

This is an unofficial fan project. It is not affiliated with or endorsed by Sony Interactive Entertainment, Polyphony Digital, Kunos Simulazioni or gro-ove.

## Features
- World map / region map / course entry screens with gamepad and wheel D-pad navigation
- Brand Central style car browser and a personal garage
- Triple-screen and 3+1 spectator (TV camera on a 4th monitor) launch modes
- Assists saved and applied at launch (including damage)
- Lap records with the conditions you drove them in
- Japanese UI (untranslated parts fall back to upstream English)

## Install
1. Download the zip from [Releases](../../releases).
2. Unzip anywhere and run `Content Manager.exe`. The bundled `Data` folder keeps all settings next to the exe, separate from a regular Content Manager install.
3. On first start, confirm your Assetto Corsa folder.

## Requirements
- Assetto Corsa (Steam) + Custom Shaders Patch
- Windows 10/11, .NET Framework 4.8
- Custom themes need your own Content Manager key from the CM author. AT7 never includes or bypasses it.

## Not included
Music, sound effects, videos and car photos are not distributed. You can add your own under `Data\Static\gt7\` (e.g. `sfx\ui_select.mp3`, `ui_move.mp3`, `ui_cancel.mp3`). Lap records are saved to `Data\Static\gt7\records`; put a folder path in `records\records_dir.txt` to store them elsewhere.

## Build
Visual Studio 2022 Build Tools with the .NET Framework 4.8 targeting pack, then:
```
MSBuild AcManager\AcManager.csproj /t:Build /p:Configuration=Release /p:Platform=x86 /p:PostBuildEvent=
```

## Contributing
Forks and pull requests are welcome. The GT-style screens live in `AcManager/Pages/Gt/`. Issues can be written in English or Japanese.

## License
Microsoft Public License (Ms-PL), same as upstream — see [LICENSE.txt](LICENSE.txt). Original code © gro-ove and contributors. Fork changes by Ynte (texis324).

---

## 日本語
Content Manager（gro-ove 作）を改造した、GT 風のメニュー画面です。ワールドマップ → 地域マップ → コースイン → ロード画面 → 走行、の流れをパッドやハンコンの十字キーだけで操作できます。

- [Releases](../../releases) から zip を落として、好きな場所に展開し、`Content Manager.exe` を起動してください。
- 必要なもの: Assetto Corsa 本体と Custom Shaders Patch
- 非公式のファン作品で、Sony / Polyphony Digital / Kunos / gro-ove とは無関係です。
- 音楽・効果音・動画・車の写真は同梱していません。CM のキーも同梱も回避もしていません。
- 改造・プルリクエスト歓迎です。
