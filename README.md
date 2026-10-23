# EasyCAP / VLC signal recovery

Windows版EasyCAP (`AV TO USB2.0`) の取り込み用スクリプトと、自作の起動・中継プログラムです。ゲーム機をリセットした際の映像信号断・復帰で、VLC 3.0.12のDirectShow入力の同期が崩れてカクつく問題に対応します。VLC本体やドライバーへのパッチではありません。

## 変更内容

- FFmpegでPCの到着時刻を映像のタイムスタンプに使用し、VLCへライブMatroskaをパイプで渡します。機器側の時刻変化からVLCのDirectShow入力を切り離します。
- 映像はMJPEGのまま転送し、再圧縮しません。現在の確認機器の720×480/25fpsを維持します。
- 音声は48kHz/16bit PCMのサンプル数からタイムスタンプを作ります。パケット到着の揺れに合わせた速度補正を避け、音の揺れを抑えます。
- プレビューには軽い色補正を適用します。同期設定やノイズ除去とは分離しています。
- `Record-EasyCAP.exe` は画面録画を介さず、取り込み開始から元の映像・音声をMKVへ保存します。終了時に再圧縮せず索引と再生時間を付け直します。

## 必要なもの

- Windows 64bit / Windows PowerShell 5.1 / .NET Framework 4.x
- VLC（実機確認は64bit版3.0.12）。通常のインストール先、または`VLC/vlc.exe`に置きます。
- FFmpeg。`tools/ffmpeg.exe`に配置してください。WindowsのDirectShow、MJPEG、PCM、Matroskaに対応するビルドが必要です。
- EasyCAPのデバイスドライバー。利用者の環境に別途インストールしてください。

VLC、FFmpeg、ドライバー、元のインストールCDのファイルは、このリポジトリに同梱していません。

## ビルド・利用

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-EasyCAP.ps1
```

- 通常の確認: `Open-EasyCAP.exe`
- 直接録画: `Record-EasyCAP.exe`（保存先は同じフォルダーの`recordings/EasyCAP-日時.mkv`）
- 終了: VLCの窓を閉じます。録画ファイルの仕上げが終わるまで中継プログラムを強制終了しないでください。

**起動直後を録画するときは、先にRecord-EasyCAP.exeを起動して入力を待機させ、それからゲーム機の電源を入れます。** 信号が復帰しても、取り込みプロセスを再起動しません。ただし、USB機器自体を抜き差しした場合の復旧は別です。

```powershell
# 音声デバイス名を明示してプレビュー
.\Capture-EasyCAP.ps1 -Preview -AudioDevice 'Microphone (USB Audio Device)'

# 任意の新しいファイルへ直接録画
.\Capture-EasyCAP.ps1 -Preview -RecordingPath 'C:\Captures\boot.mkv'

# 補正前の色で表示（保存される映像は常に補正前の入力です）
.\Capture-EasyCAP.ps1 -Preview -NaturalColor

# 従来方式での切り分け（信号復帰時の同期問題は残り得ます）
.\Capture-EasyCAP.ps1 -Preview -Backend DirectShow
```

録画は元のMJPEG映像とPCM音声のため、圧縮率の高い画面録画より容量が増えます。同名ファイルへの上書きは行いません。直接録画したMKVの色はプレビュー補正前です。色補正は表示上の効果です。

## 差分パッチ

`patches/signal-recovery.patch` は調査開始時の`Capture-EasyCAP.ps1`からの差分と、新規の自作ソースを含みます。対応する元スクリプトがある環境で次のように適用できます。

```powershell
git apply --ignore-space-change --check patches/signal-recovery.patch
git apply --ignore-space-change patches/signal-recovery.patch
```

既にこのリポジトリをcloneした場合は、修正済みのソースが揃っているためパッチ適用は不要です。

実機確認の範囲は[TESTING.md](TESTING.md)を参照してください。
