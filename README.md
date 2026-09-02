# 栖笺 PerchNote

一个安静栖息在屏幕边缘的原生 Windows 桌面便签。窗口失去焦点后会自动贴到屏幕右侧并缩成小标签，点击标签即可展开。

## 功能

- 待办与灵感两类便签
- Markdown 实时双栏预览
- 自动保存到本机 `%LOCALAPPDATA%\StickyMemo\notes.json`
- 失焦自动缩略、手动缩略、固定展开
- 系统托盘驻留
- 托盘右键导出当前便签为 Markdown 或 PDF
- 在托盘菜单中切换窗口置顶
- 搜索与分类筛选
- 无需联网、无需安装运行时（使用 Windows 自带 .NET Framework）

## 构建与运行

右键使用 PowerShell 运行 `build.ps1`，或在终端执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

构建结果位于 `dist\PerchNote.exe`。也可以双击 `启动栖笺.cmd`，它会在首次运行时自动构建。

## Markdown 支持

标题、粗体、斜体、删除线、行内代码、代码块、引用、链接、无序/有序列表、分隔线和任务列表（`- [ ]` / `- [x]`）。

## 系统托盘菜单

右键单击绿色笔记本图标，可以展开栖笺、新建便签、导出当前便签、切换窗口置顶或退出。PDF 导出采用 A4 分页排版并保留中文与 Markdown 样式。

## 快捷键

- `Ctrl+N`：新建灵感
- `Ctrl+Shift+N`：新建待办
- `Ctrl+S`：立即保存
- `Ctrl+B`：加粗选中文字
- `Ctrl+I`：斜体选中文字
- `Ctrl+M`：缩略到屏幕边缘
- `Esc`：缩略到屏幕边缘
