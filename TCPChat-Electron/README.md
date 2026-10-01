# TCP Chat 12（Electron 重构版）

用 **Electron** 重写的 TCP Chat。通信方式、消息文件格式、加密格式与 C# 版 **TCP Chat 11.7 完全互通**——同一个 WebDAV 目录、同一份 `settings.json`，两个版本可以混着用。

界面有两套：

| 模式 | 来源 |
|---|---|
| **液态玻璃** | [`electron-liquid-glass`](https://github.com/hicccc77/electron-liquid-glass)（DXGI Desktop Duplication + D3D11 + DirectComposition 原生折射面板） |
| **普通** | DSH Desktop WebUI 的 `--dsw-*` 设计令牌与视觉风格（从 `@deepseek-ai/dsh-client-ui-theme` 提取） |

> 没有服务端：程序把学校已有的 **WebDAV 服务器**当成一块公共留言板 —— 每发一条消息就在远端目录里写一个 JSON 文件，每隔几秒列一次目录把新文件拉下来。

---

## 运行时版本

| 组件 | 版本 |
|---|---|
| Electron | **44.5.1** |
| Chromium | **152** |
| Node（Electron 内置） | 24.21.0 |
| N-API | 10 |

原生插件是按 **N-API** 编的（`node-addon-api`，ABI 稳定），所以升级 Electron 不需要重新编译它就能跑。
但 `npm run pack` / `npm run dist` 仍会先跑 `build:native`，因为 headers 对了才能保证长期一致。

渲染层的 esbuild target 跟着 Electron 的 Chromium 走（`scripts/build-renderer.mjs` 里的
`CHROMIUM_BY_ELECTRON`）。**这张表查不到就报错**，不会悄悄用一个过期的 target——target 低于运行时
会白白降级语法，高于运行时会编出跑不动的代码。

打包需要 `NODE_OPTIONS=--use-system-ca`：electron-builder 26 用 Node 自带 CA 校验下载时，
在这台机器的证书链上会报 `unable to verify the first certificate`。`pack`/`dist` 脚本里已经带上。
## 快速开始

```bash
cd TCPChat-Electron
npm install
npm start
```

首次启动会让你填昵称（顶栏「设置」随时可改）。默认连 `https://dev.zhaohans.cn`，消息目录 `nw集训/学生资料临存/tcp_chat`（目录要事先存在，程序不会自动创建）。

设置与日志都在 **`%LOCALAPPDATA%\TCPChat\`**，和 C# 版共用；附件缓存在同目录的 `cache\` 下。

> ⚠️ 本机 `npm install` 时若卡在 Electron 二进制下载并报 `unable to verify the first certificate`，是中间证书没进 Node 的信任库。用系统证书库重跑即可：
> ```powershell
> $env:NODE_OPTIONS="--use-system-ca"; node node_modules/electron/install.js
> ```

---

## 命令

| 命令 | 作用 |
|---|---|
| `npm start` | 构建渲染层并启动 |
| `npm run dev` | 同上，带 DevTools |
| `npm run watch` | 监听渲染层源码，改动即时重建 |
| `npm test` | 跑协议互通测试（C# 交叉验证 + 端到端 WebDAV） |
| `npm run test:interop` | 只跑与 C# 版的加密/信封格式互通测试 |
| `npm run test:protocol` | 只跑本地 WebDAV 端到端测试 |
| `npm run build:native` | 针对 Electron 重建原生玻璃插件（见「液态玻璃」） |
| `npm run test:live` | **只读**连一次真实服务器，报告目录里有多少消息、能否解析；不发、不删、不改任何东西 |
| `npm run selftest` | 载入示例消息并截图（验证渲染，不需要服务器） |
| `npm run tokens` | 从 DSH Desktop 重新提取 `--dsw-*` 设计令牌 |
| `npm run pack` / `npm run dist` | 打包到 `release/`（免安装目录 / NSIS + 便携版） |

---

## 与 C# 版的互通性

`npm test` 里的 `test:interop` 会把 C# 版**原封不动的** `MessageCrypto.cs` / `ChatMessage.cs` 编译成一个小控制台程序（`tests/interop-csharp/`），再和本仓库的 `src/main/crypto.js` 交叉验证：

* PBKDF2 派生出的密钥逐字节一致（含中文目录做盐）
* C# 加密 → Node 解密，Node 加密 → C# 解密
* 附件字节的 `nonce‖密文‖tag` 双向互通
* 完整的 `{from,text,quote,attach}` 信封双向互通
* AAD（消息文件名）被改动时两边都解不开
* 密码列表语义一致：空项 = 明文发送，其余密码仍参与解密

`test:protocol` 会起一个进程内的 WebDAV 服务器，让两个客户端真的收发一轮。

**实测：92 项检查全部通过。**

### 对真实服务器只读验证

`npm run test:live` 会拿默认配置连一次 `https://dev.zhaohans.cn`，
走完整同步管线（PROPFIND → 过滤 → GET → 解析），**只读**，不写任何东西。实测结果：

```
entries            : 337
attachments        : 41
message files      : 294
parsed             : 8 ok, 0 malformed
initialize()       : true — 已连接
messages synced    : 294
decrypt failures   : 63      （没配密码，属预期）
with attachments   : 32
senders seen       : WZJ, Cyanclay, abc
同步完成: 294 个文件, 耗时 9523 ms
```

这 294 条消息都是 **C# 客户端写上去的真实数据**，全部解析成功、零畸形：
`v:1` 明文的 CRLF 正文、`v:2` 的 `AESGCM1:` 信封、`att_*` 附件引用都对上了。
这是除「两端同时在线」之外能拿到的最强兼容性证据。

---

## 消息格式（与 C# 版一致，不可随意更改）

```
msg_<unix毫秒>_<8位随机hex>.json    一条消息一个文件，文件名即排序键
att_<unix毫秒>_<6位随机hex>_<原名>  附件/语音，与消息同目录
```

```jsonc
// 明文
{ "v":1, "id":"…", "from":"昵称", "time":"2026-02-14T02:00:00.000Z",
  "text":"正文", "quote":"引用", "attach":{ "name":"…","path":"…","size":0,"kind":1,"dur":0 } }

// 加密（服务器上只有 enc，正文/引用/附件信息整体进密文）
{ "v":2, "id":"…", "from":"昵称", "time":"…", "text":"", "enc":"AESGCM1:…" }
```

* 密钥 = `PBKDF2-HMAC-SHA256(密码, 盐, 100000, 32)`，
  盐 = `SHA256("TCPChat10/v2|crypto|" + 消息目录)[0..16]` —— 同一个目录同一个盐
* 信封 = `"AESGCM1:" + base64(nonce(12) ‖ 密文 ‖ tag(16))`
* AAD = **消息文件名**，所以文件被改名/搬走就解不开
* 附件字节单独加密，AAD 是附件文件名
* 昵称与时间保持明文（接收方要靠它显示是谁、什么时候发的）

这套格式由 `tests/interop.test.mjs` 对着真实 C# 代码守着。

---

## 液态玻璃

原生模块 `electron-liquid-glass`：面板是一个 DirectComposition 窗口，由插件的 worker 线程用
**DXGI Desktop Duplication** 抓桌面、模糊、过镜头着色器（边缘位移 + 色散），再合成到我们窗口的
正下方。`src/main/glass.js` 只负责生命周期（创建、贴合、质量、销毁），把插件的亮度回调转成
渲染层事件。

```
┌─ Electron 窗口（透明，画文字/色调/描边）─────────────────┐
│  ┌─ 原生玻璃面板（钉在正下方，折射 + 模糊 + 色散）────┐  │
└──┴──────────────────────────────────────────────────┴──┘
```

`body[data-glass]` 只有两个值：`on`（面板在跑）和 `off`（不透明普通主题）。
面板的 `excludeFromCapture` 必须保持 `true`——否则面板会抓到自己，画面收敛成全黑。

`data-luma` 来自插件推送的亮度带（标题栏 / 消息区 / 输入区三块），亮度高就用深色字，
亮度低就用浅色字，带迟滞避免闪。桌面静止时插件不推任何东西。

### 自捕获：排除策略必须在窗口 `show` 之后再施加

`WDA_EXCLUDEFROMCAPTURE`（`GetWindowDisplayAffinity` 回读是 `all`）**在窗口还在创建时施加是不会生效的**——
回读返回的是"请求值"，不是"是否被 DWM 执行"。表现就是：日志说排除已开启，面板却照样把 app 自己的
UI 拍进去，整个窗口出现重影。

修法是在窗口 `show` 事件里重新施加一次（并延迟再补一次），另外每次 `syncBounds` 也顺手重申。

**这条结论是看图看出来的，不是推出来的。** `GLASS_DEBUG=2` 会把面板实际采到的区域纹理导成
`%TEMP%\glass-region-*.bmp`——统计数字只能告诉你"不是黑的"，告诉不了你"里面有什么"。

```
修复前：区域纹理里是 app 自己的标题栏、消息气泡、输入框、状态栏，壁纸在底下
修复后：只有干净的壁纸
```

顺带一提：之前用 WGC 截图去"证明"面板正常是无效的，因为面板带 `excludeFromCapture`，截图拍不到它，
拍到的只是它背后的桌面。**唯一不会说谎的是面板自己的输出。**
### `#app` 在玻璃模式下必须是透明的

`base.css` 里 `#app` 带的是 DSH 自己的不透明页面底色（浅色主题就是纯白）。玻璃模式下必须有规则
把它覆盖掉，否则原生面板被一张白纸盖住——**表现为整个窗口全白，而不是黑**。这条规则也是
`--surface-body` 唯一的消费点：

```css
body[data-glass='on'] #app {
  background: var(--surface-body);   /* transparent */
  border-color: transparent;
  box-shadow: inset 0 0 0 1px var(--hairline);
}
```

自检里的 `appBg` 就是盯这件事的：玻璃模式下它必须是 `rgba(0, 0, 0, 0)`。
### 背景模糊默认关闭

镜头 pass 采样的原本是**半分辨率**的模糊中间纹理，所以背景天生是软的。现在 `blurSigma <= 0.5`
会走一条清晰路径：两个卷积 pass 仍然跑，但改成**全分辨率直通**（`dir = 0` 时五个抽头落在同一个
纹素上，等价于拷贝），镜头直接读到像素级锐利的桌面裁切。

之所以不干脆跳过这两个 pass，是因为 `toBlurUV` 是按面板几何算的归一化 UV，与纹理尺寸无关；
保持 pass 存在、只改尺寸，镜头映射、亮度 staging 拷贝、纹理绑定全都不用动——两种模式走的是同一条路径。

设置里的「背景模糊」开关默认关闭，对应 `glassBlur`。
### 全黑的两类原因

**一、镜像纹理的格式必须钉死成 BGRA8。**

`UpdateDesktopCache` 一开始是照抄当前帧的 `D3D11_TEXTURE2D_DESC` 来建持久镜像的。看着无害，实际是这台机器上"全黑一片"的直接原因：即便 `capture.cc` 用 `DuplicateOutput1` 明确请求了
`B8G8R8A8_UNORM`，duplication 会话的**第一帧**在支持 HDR 的输出上仍可能交回
`R16G16B16A16_FLOAT`（DXGI format 10）。镜像只建一次，就锁在那个格式上了，之后每一帧 BGRA8
往 FP16 镜像里拷都是格式不匹配——**D3D 不报错，直接丢弃**。镜像永远是全零，面板永远是黑的。

插桩证据（`GLASS_DEBUG=1`，逐步探测 region / blur / backbuffer）：

```
修复前
source#1  fmt=87  mean=(19.4,18.5,17.6,255.0)   ← 桌面像素是真的
mirror#1  fmt=10  mean=(0.0,0.0,0.0,0.0)        ← 镜像格式错，全零
region#1  fmt=87  mean=(0.0,0.0,0.0,0.0)
backbuffer#1      mean=(1.1,1.1,1.1,254.5)      ← 上屏是黑的

修复后
source#0  fmt=87  mean=(159.1,170.8,154.5)
mirror#0  fmt=87  mean=(159.1,170.8,154.5)
blur              mean=(178.9,185.5,136.0)
backbuffer#0      mean=(188.0,195.2,127.5)      ← 上屏是亮的
```

所以现在镜像固定 BGRA8，并且**格式对不上的帧直接拒绝**，而不是让它污染缓存。

**二、验证手段本身会骗人。** 面板带 `excludeFromCapture`，Win32 捕获（WGC）**拍不到它**，
拍到的只是面板背后的桌面。拿这种截图当"面板正常"的证据是错的——真正不会说谎的是
`backbuffer` 那一行：它是唯一真的上屏的东西。
### 构建原生模块是必须的

**`vendor/electron-liquid-glass/` 是本仓库的源码构建，不是 npm 上的预编译二进制。**
这一条不是洁癖，是踩过的坑：

* **必须针对 Electron 的 headers 构建，不能用 Node 的。** 用 Node 的 headers 编出来的
  `.node` 能加载、能跑，但 N-API 返回值是坏的：`createPanel` 交回来的面板 id 是
  `6.2114605e-317`（整数 1 的位模式被当成 double 读），`Int32Value()` 取出来是 0，
  于是**所有按 id 的调用全部静默失效**——面板建好了却永远 `Show()` 不到，一帧都不渲染。
* Node 24 的 `common.gypi` 把 `msbuild_toolset` 钉死成 `ClangCL`，没装 ClangCL 组件
  会直接 MSB8020 失败。
* node-gyp 9 仍然 `import distutils`，而 Python 3.12 已经移除它，需要 `setuptools` 补回来。

```
npm run build:native
```

脚本自己找 Python、指定 `--target=<electron 版本> --dist-url=https://electronjs.org/headers`。
`npm run pack` / `npm run dist` 都会先跑它，并且 `electron-builder.yml` 里关掉了
`npmRebuild`——electron-builder 自己的重建会拿 Node 的 headers 再编一遍，正好编出上面那个坏二进制。
## 普通主题：DSH Desktop WebUI 设计令牌

`src/renderer/styles/tokens.css` 是从 DSH Desktop 的
`@deepseek-ai/dsh-client-ui-theme` 里**逐字提取**的设计系统（26 条规则、583 个自定义属性、99 个语义别名），
不是照着截图调的近似色。提取脚本：`scripts/extract-dsh-tokens.mjs`（`npm run tokens`）。

沿用了 DSH 自己的约定：**深色模式 = `<body>` 上的 `data-ds-dark-theme` 属性**，
和 DSH 的启动脚本完全一致，所以明暗两套别名可以直接用。

聊天界面在这套令牌上扩展：

* 画布用 `--dsw-alias-bg-document-preview`（浅色 `#ebeef2` / 深色 `#151517`），
  气泡用 `--dsw-alias-bg-layer-1` + 描边 —— 因为 DSH 浅色把 layer-1/2/3 都设成纯白，
  靠描边分层；气泡需要真实填充才看得出来
* 自己的消息用 `--dsw-alias-state-business-primary`（DeepSeek 蓝）：
  浅色下 `#4176e6` 配白字，深色下 `#7aaaff` 配墨色字
* 代码高亮的每个 token 都映射到 DSH 调色板

> 有个 CSS 细节值得记一笔：引用 `--dsw-*` 的语义变量**必须声明在 `body` 上而不是 `:root`**。
> 自定义属性里的 `var()` 是在**声明它的那个元素**上求值的，而所有 `--dsw-*` 别名都在 `body`
> （深色那套在 `body[data-ds-dark-theme]`）。写在 `:root` 上会在别名还不存在时求值，
> 整条声明直接失效，背景变成透明 —— 不报错，只是看不见。

---

## Markdown / LaTeX

用 `markdown-it` + `katex`，按 C# 版的行为配置（`html: false`，不解析原始 HTML，和洛谷一致）：

* **纯文本快路径**：不像 markdown 的消息原样显示 —— `3 - 2 = 1`、`1.5 倍`、`a_b_c` 不会被改写；
  超过 2 万字符直接走纯文本，保证滚动流畅
* **洛谷扩展**：`:::info[标题]` / `success` / `warning` / `error`（`{open}` 默认展开、可嵌套）、
  `:::align{center}`、`:::epigraph[——作者]`；代码块 `line-numbers` 行号 + `lines=6-9` 高亮；
  表格 `^` 向上合并 / `<` 向左合并、`::cute-table{tuack}`
* `> [!NOTE]` 提示块、任务列表、脚注、定义列表、`:smile:`、`==高亮==`、`H~2~O`、`x^2^`
* **LaTeX**：`$…$` / `\(…\)` 行内，`$$…$$` / `\[…\]` 块级；
  代码块里的 `$` 不排版；系统通知与引用里退化成可读纯文本（`\frac{a}{b}` → `(a)/(b)`）

代码块的每行都是块级 `<span class="ln">` —— 只给高亮行加块级、其余行留行内的话，
`white-space: pre` 保留下来的换行会让高亮行多出一截行距，行号和代码就对不齐了。

---

## 功能

**消息**：Enter 发送 / Shift+Enter、Ctrl+Enter 换行；Markdown + LaTeX 渲染；文件、图片、视频、音频、语音；
引用回复 / 复制文本 / 撤回（连附件一起删）；新消息系统通知 + 任务栏闪烁；自动滚到底部（可关）；
本地缓存启动即显，对方撤回同步移除；一条消息一个文件，无并发写冲突。

**语音**：🎤 开始录，再点一下停止并发送。Chromium 录出来是 WebM/Opus，但 C# 版会把 `.webm`
当成**视频**处理（显示黑色缩略图），所以这里先解码再用 Web Audio 重新编码成 **16-bit PCM WAV**，
文件名沿用 `语音 N秒.wav`。短于 700ms 丢弃。没有麦克风权限时弹窗并打开系统隐私设置页，
用户打开开关回到窗口后**自动开始录音**（和 C# 版一致）。

**附件**：通过自定义协议 `tcpcache://` 提供本地缓存的附件，带 Range 支持，
所以音视频能拖动进度条。图片/视频用 IntersectionObserver 懒加载 —— 打开很长的历史不会一次性下载所有附件。
视频用 `#t=0.1` 让 Chromium 解出第一帧当缩略图（对应 C# 版的「取第一帧」）。

**界面**：深浅色（跟随系统/强制）；字体（可下拉选本机字体或直接输入，带实时预览）；
缩放 60%~240%（Ctrl +/+/-/0、Ctrl+滚轮，写进设置）；搜索（Ctrl+F，Enter/Shift+Enter 跳转、
Esc 退出、底栏显示进度）；右键菜单；图片灯箱；首次启动自动弹出设置让你填昵称。

**安全**：`contextIsolation: true`、`nodeIntegration: false`、`sandbox: true`；
严格 CSP；只放行麦克风一类权限；主进程里限制导航与新窗口，外链一律交给系统浏览器。
渲染进程只能通过 preload 暴露的 `window.tcpchat` 说话，拿不到任何文件系统、网络或加密原语。

---

## 目录结构

```
src/
├── main/                    主进程
│   ├── index.js             窗口、IPC、协议、权限、启动
│   ├── settings.js          设置（与 C# 版同一份 JSON）
│   ├── paths.js             数据目录 + 老目录迁移
│   ├── logger.js            %LOCALAPPDATA%\TCPChat\crash.log
│   ├── crypto.js            AES-256-GCM 信封 / MessageCipher
│   ├── webdav.js            极简 WebDAV 客户端 + DAV XML 解析
│   ├── chat-service.js      同步轮询、发送、撤回、附件
│   ├── message-cache.js     本地消息缓存
│   ├── message-store.js     主进程持有的有序消息表
│   ├── glass.js             原生玻璃面板生命周期 + 亮度带
│   └── selftest.js          示例数据 + 截图自检
├── preload/index.js         contextBridge 契约
└── renderer/
    ├── index.html
    ├── styles/              tokens(生成) / base / app / markdown / glass
    └── js/                  markdown, messages, composer, recorder,
                             attachments, search, settings-ui, overlays, main
                             settings-ui, overlays, main
```

---

## 已知限制

* **液态玻璃需要 Windows 10 2004+，且原生模块必须是本地构建的**：`isSupported()` 为假时
  自动退回不透明普通主题，设置里会写明原因。
* **玻璃面板拍不进截图**：这是 `excludeFromCapture` 的语义，也是避免自采集回环的前提。
* **桌面静止时不推送亮度**：插件是重绘驱动的，静态桌面上没有回调是正常的。
* **拖放文件暂不支持**：渲染进程拿不到拖入文件的真实路径（Electron 的安全模型所限），
  会提示改用「📎 文件」按钮。
* **无边框窗口的缩放是 DOM 实现的**：Windows 对 `transparent: true` 的窗口不提供原生缩放边框。
## 打包

```bash
npm run dist        # release/TCP-Chat-12.0.0-setup.exe + portable
```

原生模块必须解出 asar 才能 `dlopen`，配置里已经写好：

```yaml
asarUnpack:
  - '**/node_modules/@hicccc77/electron-liquid-glass/**'
```

`deleteAppDataOnUninstall: false` —— 配置文件与 C# 版共用，卸载不能删用户数据。

---

## 来源与许可

* `vendor/electron-liquid-glass/` 是 [`@hicccc77/electron-liquid-glass`](https://github.com/hicccc77/electron-liquid-glass) 0.4.0 的随包分发副本（MIT），
  含 `prebuilds/win32-x64` 预编译二进制。本机没有 MSVC C++ 工具链与 Python，
  无法从 `electron-liquid-glass-main/` 的源码编译，因此使用官方发布版的 JS + 二进制配对
  （两者必须同版本，`index.js` 会按 `capturePolicy` 参数与原生侧对接）。
* DSH Desktop WebUI 设计令牌来自 DeepSeek Harness 的 `@deepseek-ai/dsh-client-ui-theme`，
  仅提取配色/字体/圆角/层次等设计变量用于本项目的普通主题。
* 其余代码 MIT。
