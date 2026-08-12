# denpa-agent-windows

[denpa](https://github.com/danything/denpa) の**チューナーエージェントの Windows 版**。
BonDriver でチューナーを掴み、denpa と同じ HTTP 契約 (`/denpa/stream` ほか) で TS を流す。

Linux 版 (denpa 本体の `agent/`) が `/dev/dvb` を ioctl で叩くのに対し、こちらは
**`BonDriver_*.dll` を読み込んで選局**する。それ以外 — 優先度つきの取り合い・
チャンネル表・SSE・B25 解除・録画中の居座り (graceful shutdown) — は Linux 版と同じ。

> [!WARNING]
> **未実機検証。** ABI の定義どおりに書いてあるが、実機の BonDriver・B-CAS カードでの
> 動作確認はこれから。マネージドのコンパイルは通っている (CI)。下の「検証したいこと」参照。

---

## 何をするか

denpa は「録画したい」「ライブで見たい」「番組表を集めたい」を **HTTP で** このエージェントに
頼む。エージェントは電波を掴んで TS を返すだけで、**中身 (NIT/SDT/EIT) は一切読まない**。

denpa との口 (Linux 版とまったく同じ):

| 口 | 役目 |
| --- | --- |
| `GET /denpa/stream?type=&channel=&priority=&use=` | 選局して TS を流す。取り合いは優先度で決める (409 で「空き無し」) |
| `GET /denpa/events` | チューナーの様子を SSE で流す |
| `GET/PUT /denpa/tuners` | 繋いである機材の一覧・更新 |
| `GET/PUT /denpa/channels` | スキャン結果の預かり (denpa が書く) |
| `GET /denpa/card` | カードリーダーが見えているか (WinSCard) |
| `POST /denpa/decode` | 掛かったまま録れた TS を後から解く |
| `GET /denpa/card/init` `POST /denpa/card/ecm` | 鍵の配布 (1枚のカードを複数拠点で共有) |

## Linux 版との違い

| | Linux 版 | この Windows 版 |
| --- | --- | --- |
| 選局 | `/dev/dvb/*` を ioctl | **BonDriver** (`BonDriver_*.dll`、IBonDriver2) |
| チャンネル指定 | 周波数 (選局表) | **(space, channel) 索引**。物理ch との対応表が要る (下記) |
| カード | pcscd + pcsc_scan | **WinSCard** (OS 標準。デーモン不要) |
| B25 解除 | `libaribb25` (Linux) | `aribb25.dll` (Windows。中身は WinSCard 越し) |
| 常駐の見え方 | コンテナ | **通知領域 (タスクトレイ) に常駐** |

## 必要なもの (実行時)

単一 exe だが、**ネイティブの相方が2つ要る** (exe と同じフォルダに置く):

1. **`BonDriver_*.dll`** — 使うチューナーのぶん。付属の `.ini` も一緒に。
2. **`aribb25.dll`** — B-CAS 解除。無くても録画はできる (掛かったまま録れて、denpa 側が
   気付く)。[aribb25 の Windows ビルド](https://github.com/nanpuh/aribb25) など。
3. WinSCard は Windows 標準 (スマートカードサービス)。追加不要。

> [!IMPORTANT]
> **ビット幅を合わせること。** 多くの BonDriver は 32bit (x86)。その DLL を読むなら
> エージェントも **x86** でないと `LoadLibrary` が弾かれる。64bit の BonDriver なら x64。
> `aribb25.dll` も同じビット幅で揃える。

## 入手

[Releases](https://github.com/danything/denpa-agent-windows/releases) に、タグを切るたび
Windows でビルドした zip が付く (`denpa-agent-win-x64.zip` / `-x86.zip`)。使う BonDriver の
ビット幅に合うほうを解けば、`denpa-agent.exe` と設定例が入っている。自分でビルドするなら下記。

## ビルド

Windows + [.NET 10 SDK](https://dotnet.microsoft.com/) で:

```powershell
cd Denpa.Agent.Windows

# 64bit BonDriver を使う場合 (Native AOT・単一 exe。いちばん軽い)
dotnet publish -c Release -r win-x64 -o ..\publish\x64

# 32bit BonDriver を使う場合 (AOT は x86 非対応なので self-contained 単一ファイル)
dotnet publish -c Release -r win-x86 -o ..\publish\x86 `
  -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=true
```

出来た `denpa-agent.exe` と、`BonDriver_*.dll` / `aribb25.dll` / `bondriver-map.json` /
`tuners.json` を同じフォルダに置く。

## 設定

### `tuners.json` — 繋いである機材

`device` に **BonDriver の DLL パス**を書く (Linux 版の `/dev/dvb/...` の代わり)。
[tuners.example.json](Denpa.Agent.Windows/tuners.example.json) を写して使う。denpa の
チューナー画面からも編集できる。

### `bondriver-map.json` — 物理チャンネル → (space, channel)

**BonDriver は周波数を受け取らない。** どの space/channel が何の局かは DLL ごとに
違うので、denpa の物理ch表記 (`T27` / `BS15_0`) との対応を**こちらで持つ**。

```json
{ "T27": [0, 14], "BS15_0": [0, 0] }
```

索引は BonDriver の定義 (`.ini`) を見て埋める。列挙もできる:

```powershell
denpa-agent.exe --enum C:\BonDriver\BonDriver_PX4-T.dll
```

### `channels.json`

denpa が `PUT /denpa/channels` で書く。触らなくてよい。

### 環境変数

| 変数 | 既定 | 役目 |
| --- | --- | --- |
| `AGENT_PORT` | `25252` | 待ち受けポート |
| `TUNERS_FILE` | `/app-config/tuners.json` | 機材定義。Windows では実パスを指定 |
| `CHANNELS_FILE` | `/app-config/channels.json` | 同上 |
| `BONDRIVER_MAP` | 実行フォルダの `bondriver-map.json` | チャンネル対応表 |
| `RECORDED_DIR` | `/denpa-recorded` | `decode` が読む生TSの置き場 (denpa と共有) |
| `CARD_URL` | (なし) | 手元にカードが無い拠点で、鍵を別の機から貰う |
| `SHUTDOWN_WAIT` | 6時間(ms) | 止められても録画が終わるまで居座る上限 |
| `NO_TRAY` | (なし) | `1` でトレイ常駐を切る (サービス運用・ヘッドレス向け) |

## 常駐 (トレイ)

exe を実行すると**通知領域にアイコンが出て「起動中」**になる (BonDriver のツールと同じ流儀)。

- **右クリック** → 「状態を開く」(カードの状態ページ) / 「終了」
- **ダブルクリック** → 状態を開く
- WinForms/WPF は使わず Win32 (`Shell_NotifyIcon`) を直に叩くので、単一 exe・AOT のまま。
- サービスやヘッドレスで動かすなら `NO_TRAY=1`。

## 検証したいこと (実機で)

コードは ABI どおりだが、以下は**実機でしか確かめられない**:

- [ ] `BonDriver_*.dll` の読み込みと `CreateBonDriver` / `OpenTuner`
- [ ] IBonDriver2 の vtable 索引 (特に `SetChannel(space,channel)` = 12、`GetTsStream` ポインタ版 = 7) が実ドライバと合うか
- [ ] `bondriver-map.json` の索引 (GR/BS/CS それぞれ)
- [ ] `aribb25.dll` での B25 解除 + WinSCard 越しの B-CAS 読み取り
- [ ] トレイの表示・右クリックメニュー
- [ ] ビット幅 (x86 self-contained 単一ファイルが実機で動くか)

## 元プロジェクト

- [danything/denpa](https://github.com/danything/denpa) — 本体 (SvelteKit + bun)。`agent/` に Linux 版
