# EasyCAP / VLC signal recovery

Windows版EasyCAP (`AV TO USB2.0`) の取り込み用スクリプトと、自作の起動・中継プログラムです。ゲーム機をリセットした際の映像信号断・復帰で、VLC 3.0.12のDirectShow入力の同期が崩れてカクつく問題に対応します。VLC本体やドライバーへのパッチではありません。

## 2026-11-27の実機再調査

GUIで「起動した」状態と、実際に映像が進んでいる状態を区別するよう修正しました。FFmpegのフレーム進行を監視し、入力待ち・入力停止・復帰を表示します。起動失敗を正常終了と誤認していた処理も修正しています。

調査した機器では、48kHz・短い音声バッファーを固定した条件で取得が止まりました。機器の標準形式（この環境では44.1kHz）での連続取得を確認したため、入力サンプルレートとチャンネル数の固定を外し、音声バッファーの既定を100msに変更しています。`-AudioBufferMs`で調整できます。

**同じ機器から黒い映像しか届かない問題は未解決です。** 別USBポート、MJPEGと未圧縮映像、Windowsカメラでも黒画面を確認しています。テレビへの直接接続では表示されるとの利用者確認があり、機器の映像入力または共通ドライバー側を引き続き切り分ける必要があります。GUIの動作確認とゲーム画面の取得成功を混同しないでください。

## 変更内容

- FFmpegのDirectShowグラフ時計（PC側の時計）を映像のタイムスタンプに使用し、VLCへライブMatroskaをパイプで渡します。機器由来の映像タイムスタンプを使わず、信号復帰による時刻の乱れを切り離します。
- 映像はMJPEGのまま転送し、再圧縮しません。現在の確認機器の720×480/25fpsを維持します。
- 音声は機器の入力サンプルレートを使い、開始時刻を保持してPCMのサンプル数からタイムスタンプを作ります（`asetpts=STARTPTS+N/SR/TB`）。パケット到着の揺れに合わせた速度補正を避け、音の揺れを抑えます。
- 入力音声バッファーは100msで要求し、入力解析100ms、VLCのファイル／ネットワークキャッシュ50ms、先読み容量128KiBを明示します。これらは各段の設定値であり、総遅延の保証値ではありません。
- プレビューには軽い色補正を適用します。同期設定やノイズ除去とは分離しています。
- `Record-EasyCAP.exe` は画面録画を介さず、取り込み開始から元の映像・音声をMKVへ保存します。終了時に再圧縮せず索引と再生時間を付け直します。
- `EasyCAP.exe` / `Open-EasyCAP.exe` のGUIから、プレビュー・録画・停止・保存先変更・色補正の切り替えができます。起動後に動作時間と保存ファイルを表示します。
- キャプチャーからVLCを起動するときは、初回のPrivacy and Network Access Policy画面を表示せず、メタデータのネット取得と更新通知も無効にします。通常のVLC起動や保存済みの設定は変更しません。機器エラーなどの警告は隠しません。

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

- 操作画面: `EasyCAP.exe` または `Open-EasyCAP.exe`。青の「プレビュー起動」は保存なし、赤の「録画して起動」は表示しながら直接録画します。
- 保存先は画面から変更できます。既定は同じフォルダーの`recordings/EasyCAP-日時.mkv`で、選択した保存先と色補正の有無を次回に引き継ぎます。
- 終了: 操作画面の「停止」またはVLCを閉じます。録画時はファイルの仕上げが完了するまで操作画面が待ちます。操作画面自体を閉じても、停止と保存を終えてから終了します。
- 従来の直接起動: `Open-EasyCAP.exe --preview`、直接録画: `Record-EasyCAP.exe` / `Open-EasyCAP.exe --record`。これらも引き続き使えます。

**起動直後を録画するときは、GUIの「録画して起動」（またはRecord-EasyCAP.exe）で先に取り込みを開始してから、ゲーム機の電源を入れます。** 信号が復帰しても、取り込みプロセスを再起動しません。ただし、USB機器自体を抜き差しした場合の復旧は別です。

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
