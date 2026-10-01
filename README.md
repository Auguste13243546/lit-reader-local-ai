# LitReader · 本地 AI 阅读助手

一个**完全本地运行**的桌面 AI 助手，为「一边读文献一边查专业术语」设计。

按一个热键唤出一个浮窗，划词即问，答完自动归位，不打断你的阅读。
**不调用任何云 API**，你的文献、提问、术语本全部留在自己机器上。

界面采用**新粗野主义（Neo-Brutalism）**风格：硬边缘、粗黑描边、硬偏移阴影、高饱和撞色。

![界面预览](docs/mockups/04_final_chosen.png)

---

## ✨ 功能

### 文献术语模式（默认）
- **划词即问**：在浏览器 / Word / PDF 阅读器里拖动选中文字，松手后**自动读取并发问**，无需复制粘贴
- **术语解释**：回答经过定制提示词约束，先给准确中文定义 → 再讲语境作用 → 必要时补英文全称与类比
- **学术词汇自动入库**：每次回答后异步抽取专业术语，累积到「词汇总表」
- **★ 收藏**：把某个术语一键存入术语本
- **术语本**：侧栏列出所有收藏，点术语可重新提问，**右上角 ✕ 可快捷删除**

### 生活助手模式
- **独立上下文**，与文献模式互不串味
- **跨会话长期记忆**：对话结束（空闲 10 分钟 / 点「新对话」/ 切换模式）后自动总结，落盘存档**并回读注入**下次对话
- 三层记忆结构：滚动总记忆 + 每次会话的独立摘要 + 原始存档

### 工作助手模式
面向日常办公写作与资料处理，提示词刻意**不继承**文献模式「200 字以内」的约束 ——
那条规则会把周报、纪要、汇总全部压成残句。工作模式的要求是：

- **产出可直接使用的成品**，不写「以下是一份…」这类空话，也不复述你的要求
- **篇幅按任务需要决定**，该长就长、该短就短
- 结构化输出优先用标题 / 编号 / Markdown 表格；公文用规范中文书面语
- 信息和数字不足时，先给基于现有内容的可用版本，再把缺口列成问题
- 代码与报错排查给出可运行的完整片段，并说明改了什么

同样具备**独立上下文**与**跨会话长期记忆**，记忆存放在 `data\工作记忆\工作\`，
与生活记忆（`data\工作记忆\生活\`）分开，互不污染。

### 通用
- **全局热键**唤出 / 收起（默认 `Alt+Z`，可自动降级）
- **焦点不抢占**：唤出与回答完毕时，键盘焦点都留在你原来的窗口，可直接继续在原文操作
- **消息锚点标尺**：右侧等距横杠 = 每轮问答一个锚点，悬停变红加长，点击跳转
- **平滑非线性滚动**：指数缓动 + 边界阻尼，滚轮有惯性
- **Markdown 渲染**：标题 / 列表 / 引用 / 表格 / 代码块 / 行内样式（零依赖自研渲染器）
- **图片问答**：`Ctrl+V` 粘贴截图或点「图」导入，交给带视觉投影层的模型解析
- **思考模式开关**：关掉可显著提速，需要深挖时再开
- **开机静默启动**：只留托盘图标，按热键才出现

---

## 🖥 环境要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 / 11（x64） |
| 编译工具 | MSBuild + WPF 目标（.NET Framework Developer Pack / SDK 或 VS Build Tools） |
| 运行时 | .NET Framework 4.7.2+（Win10/11 系统自带，无需额外安装） |
| 显卡 | **建议 NVIDIA 独显，显存 ≥ 8GB**（用于本地推理） |
| 内存 | 建议 ≥ 16GB |
| 推理引擎 | [Ollama](https://ollama.com/download) |

> 说明：本项目是 **Windows 桌面程序**（WPF + Win32 API），依赖 `SetWindowsHookEx` 全局钩子、UI Automation 与 Win32 窗口操作，**无法在 Linux / macOS 上编译运行**。

---

## 🚀 快速开始

### 1. 安装 Ollama 并拉取模型

```powershell
# 从 https://ollama.com/download 安装后：
ollama pull qwen3.5:9b
```

> **提示**：模型名可自定义，见下文「配置」。
> 若你的网络无法访问 `registry.ollama.ai`，可改用 ModelScope 下载 GGUF 后本地导入。

### 2. 编译

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

若你的 .NET Framework 不是 4.8：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -TargetFramework v4.7.2
```

### 3. 安装并创建快捷方式

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-app.ps1
```

默认安装到 `%LOCALAPPDATA%\LitReader`，可用 `-InstallDir` 指定：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-app.ps1 -InstallDir "D:\Apps\LitReader"
# 需要开机自启（静默启动，只留托盘图标）时加 -AutoStart
```

### 4. 让 Ollama 开机自启（可选但推荐）

Ollama 自带的托盘程序**不会继承自定义环境变量**，重启后可能去找错误的模型目录，导致每次提问都卡住。这个脚本会修正它：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-ollama-autostart.ps1
# 自定义路径：
#   -OllamaExe "D:\Ollama\ollama.exe" -ModelsDir "D:\Ollama\models"
```

### 5. 按你的显卡标定参数（重要）

上面的脚本会写入一组**针对 8GB 显存笔记本实测调优**的环境变量：

| 变量 | 值 | 依据 |
|---|---|---|
| `OLLAMA_FLASH_ATTENTION` | `1` | **必须开**。关掉时 KV 缓存过大，约 13% 的层掉到 CPU，生成速度从 57 掉到 **11.8 tok/s（慢 5 倍）** |
| `OLLAMA_NUM_THREAD` | `12` | 实测甜点。设成全部 24 线程反而**慢 17%**（P/E 混合核争抢内存带宽） |
| `OLLAMA_CONTEXT_LENGTH` | `24576` | 24K 是仍能 100% 驻留 GPU 的最大窗口 |
| `OLLAMA_NUM_PARALLEL` | `1` | 单人桌面应用，并发槽会成倍放大 KV 缓存 |
| `OLLAMA_KEEP_ALIVE` | `30m` | 模型常驻，按下热键立刻回答而不是等冷加载 |
| `OLLAMA_VULKAN` | `false` | 防止混合输出笔记本误选 Intel 核显 |

启动脚本还会**主动预热模型**：`ollama serve` 本身不加载权重，不预热的话本次开机
第一次提问仍要等 5～10 秒冷加载。

> **换显卡或换模型后请重新标定。** 方法与完整数据见 [`docs/tuning.md`](docs/tuning.md)，
> 里面有可直接复用的扫描脚本思路与全部实测表格。

---

## 🎮 使用

| 操作 | 说明 |
|---|---|
| **`Alt+Z`** | 唤出 / 收起浮窗（`Win+Z` 被系统贴靠布局占用，故默认用 `Alt+Z`） |
| **拖动选中文字** | 自动读取并发问（浏览器 / Word / 阅读器有效） |
| `Esc` | 收起浮窗 |
| `Ctrl+V` | 粘贴截图提问 |
| `Enter` / `Shift+Enter` | 发送 / 换行 |
| **右侧横杠** | 每轮问答一个锚点，悬停变红，点击跳到该轮 |
| 「文献 / 生活 / 工作」 | 每点一次切换一个模式：**文献 → 生活 → 工作 → 文献**，三者上下文互相独立 |
| 「术语本」 | 展开术语本侧栏 |
| 「★ 收藏」 | 收藏该轮提问里的术语 |
| 术语条 **✕** | 从术语本删除该术语 |
| 「思考」 | 开关思考模式（关掉显著提速） |
| 「视觉」 | 切换带视觉投影层的模型 |
| 托盘图标 | 双击唤出，右键退出 |

### 配置文件

程序首次运行会在**自身目录**生成 `config.txt`：

```ini
# 数据目录：长期记忆、学术词汇、术语本都存放在这里
DataDir=...\LitReader\data

# Ollama 服务地址
OllamaBase=http://127.0.0.1:11434
```

也可用环境变量覆盖（优先级高于配置文件）：

```powershell
$env:LITREADER_DATA_DIR = 'D:\MyData'
$env:LITREADER_OLLAMA   = 'http://127.0.0.1:11434'
```

改模型名请编辑 `src/FloatWindow.xaml.cs` 顶部：

```csharp
const string ModelText   = "lit-reader";        // 文本模型
const string ModelVision = "lit-reader-vision"; // 带视觉投影层的模型
const int    CtxText     = 24576;               // 文本上下文（见 docs/tuning.md）
const int    CtxVision   = 12288;               // 视觉上下文（带投影层，更吃显存）
```

> `CtxText = 24576` 是在 8GB 显存上实测出来的**满速上限**：24K 仍能 100% 驻留 GPU，
> 32K 就会有约 11% 的层掉到 CPU、生成速度腰斩。三个模式共用同一个 `CtxText`，
> 否则 Ollama 会按不同的上下文长度同时驻留两份权重，把显存挤爆。

创建自定义模型（含术语解释人设）：

```powershell
ollama create lit-reader -f Modelfile
```

`Modelfile` 示例：

```
FROM qwen3.5:9b
PARAMETER num_ctx 24576
PARAMETER temperature 0.3
SYSTEM """
你是一位学术文献阅读助手，专长是解释各学科的专业术语。
先用一句话给出准确中文定义，再说明它在当前文献语境中的作用，
必要时补充英文全称与类比。不确定时明确说明存在歧义，绝不编造。
"""
```

> 注意：`PARAMETER num_ctx` 的优先级**高于**环境变量 `OLLAMA_CONTEXT_LENGTH`。
> 请求里显式传的 `options.num_ctx` 又高于两者 —— 程序就是靠这一点在不同模式间
> 统一下发上下文长度。

### 数据目录结构

```
<程序目录>\data\
├── 学术词汇\
│   ├── 词汇总表.md          # 自动抽取累积，按术语去重
│   ├── 我的收藏.md          # ★ 收藏的术语
│   └── 会话记录\            # 每次抽取的原始存档
└── 工作记忆\
    ├── 生活\
    │   ├── 长期记忆.md      # 滚动记忆，下次会话会注入上下文
    │   └── 会话记录\        # 每次会话的独立摘要
    └── 工作\
        ├── 长期记忆.md
        └── 会话记录\
```

都是纯 Markdown，**可直接编辑或删除**（删掉即等于让 AI 忘掉）。

> 从旧版本升级：生活记忆原来放在 `data\生活\` 下，程序会在首次进入生活模式时
> **自动复制**到 `data\工作记忆\生活\`，不会丢内容。

---

## 🏗 项目结构

```
src/
├── App.cs                 程序入口：单实例、托盘图标、启动参数
├── AppConfig.cs           配置：数据目录与 Ollama 地址（无硬编码路径）
├── FloatWindow.xaml       界面（新粗野主义样式）
├── FloatWindow.xaml.cs    主逻辑：热键、划词、三模式、记忆、术语本
├── MouseHook.cs           全局鼠标钩子 + 键盘钩子（Esc 兜底）
├── SelectionReader.cs     取选区（UIA TextPattern 优先，Ctrl+C 回退）+ 窗口焦点控制
├── MarkdownRenderer.cs    零依赖 Markdown 渲染器
├── SmoothScroll.cs        非线性平滑滚动
└── LitReader.csproj       MSBuild 工程（WPF，v4.x）
scripts/
├── build.ps1                     编译
├── install-app.ps1               安装 + 快捷方式
└── setup-ollama-autostart.ps1    修正 Ollama 模型目录并配置开机自启
docs/mockups/                     界面设计概念图
```

---

## 🔧 实现要点（踩过的坑）

这些都是实测定位出来的问题与取舍，记录下来供参考：

**1. 浏览器里模拟 `Ctrl+C` 取选区行不通**
Chromium 会检查 `SendInput` 注入事件的 `LLKHF_INJECTED` 标志并**丢弃**合成按键。
改用 **UI Automation 的 `TextPattern`** 直接读选区，绕开键盘与剪贴板，零副作用。
关键陷阱：**不能用 `HasKeyboardFocus` 找元素** —— Chromium 不报告该属性，
必须「遍历树，找支持 `TextPattern` 且选区非空的元素」。

**2. 浮窗一旦成为激活窗口，浏览器就不再暴露选区**
实测：浮窗激活后 `TextPattern` 数量从 540 骤降到 2，划词必然失败。
所以唤出/启动时都用 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` ——
**抬到最上层但不抢焦点**，并把焦点还给原来的程序。

**3. `AttachThreadInput` + `SetFocus` 的副作用不会自动撤销**
为发 `Ctrl+C` 而把键盘焦点交给目标程序后，即使释放输入队列连接，
焦点仍留在外部程序 → 表现为「窗口在前台却打不进字」。
必须显式把焦点收回来（或干脆不抢）。

**4. 悬停改边框粗细会让整排按钮"震动"**
按钮宽度是内容自适应的，边框从 2.5px 变 3.5px 会改变尺寸，
`StackPanel` 里后面的按钮全被挤走。改为**叠加一层描边**，零尺寸变化。

**5. WPF 的 `Border` 不裁剪子元素的圆角**
画圆形按钮必须用 `Ellipse` 或让 `Border` 自己带 `CornerRadius`。

**6. 平滑滚动与滚动条拖拽会抢控制权**
自己缓动的同时 `ScrollBar` 也在设偏移 → 位置闪跳。
用「自身滚动预算」精确区分：每次自己滚动前计数 +1，`ScrollChanged` 配对 -1，
配不上就说明是用户拖拽，立即交出控制权。

**7. `MSG` 结构体必须完整声明**
少字段会让 `GetMessageW` 写越界，破坏内存。

---

## ⚠️ 已知限制

- **仅支持 Windows**（依赖 WPF + Win32 全局钩子 + UI Automation）
- **需要 NVIDIA 独显**才能获得可接受的速度；纯 CPU 跑 9B 模型体验很差
- 划词自动发问依赖 **UI Automation**，在部分程序里可能读不到选区（此时会静默跳过，不会误发）
- 视觉模式显存占用高（9B + 456M 投影层），因此视觉上下文单独降到 **12288**
- **单次约可容纳 4 万汉字**（24K 上下文 × 实测 0.616 token/汉字）。更长的文档需要
  自己分块，不能指望一次塞进去 —— 这不是软件限制，是 8GB 显存的物理上限
- 生活 / 工作助手的「会话结束」按**空闲超时**判定，超时期间若程序退出，本次会话不会被总结
- **系统内存（16GB）通常比显存更紧张**：模型驻留后仍会占用约 1.5～2GB，
  同时开 Word / Excel / 浏览器时容易被换页拖慢

> 本机全部实测数据（上下文扫描、Flash Attention、线程数对比、中文分词比例）
> 见 [`docs/tuning.md`](docs/tuning.md)，换机器后可按同样方法重新标定。

---

## 📄 许可

[MIT](LICENSE) —— 可自由使用、修改、分发。

## 🙏 致谢

- [Ollama](https://ollama.com) —— 本地推理运行时
- [Qwen](https://github.com/QwenLM) —— 默认使用的开源模型
