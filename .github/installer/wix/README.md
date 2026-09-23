# Files max 安装器

安装器继续使用 WiX Toolset 7 的 MSI + Burn 组合：

- `Files max Installer.msi` 负责应用文件、快捷方式、资源管理器右键集成、身份注册、修复和卸载。
- `Files max Setup.exe` 负责用户可见的安装界面、运行库前置依赖、升级检测、缓存和安装器卸载入口。

GitHub Actions 的 Files max 发布产物现在只有一个 `Files max Setup.exe`。旧的
`Files max Setup.version.json` 不再随安装包发布：产品名和版本由 Burn Bundle 本身携带，
更新检查从 GitHub Release 资产的 `digest` 读取 SHA-256，并在启动安装器前重新校验下载文件。
更新代码仍兼容历史 Release 中的 JSON 文件，因此升级链路不会因为移除附属文件而中断。

安装器使用 WiX Burn 作为可靠的检测、计划、提权、缓存、升级、修复和卸载引擎，但不再使用
WiX 官方的 WixStdBA 界面。`.github/installer/ba` 中的 WPF Bootstrapper Application
完全接管用户可见的安装向导：自定义圆角窗口、标题栏、按钮、许可勾选、安装路径页、进度页、
成功页和失败页都由 Files max 控制，以便和设计稿逐像素对齐。

Custom BA 以 `win-x64` 自包含单文件方式发布，然后作为 Burn 的 Bootstrapper Application
嵌入最终 Bundle。用户下载和解压后仍然只有一个 `Files max Setup.exe`，不会看到 BA 的 DLL、
主题图片或版本 JSON；安装引擎和自定义界面都在这个单文件安装程序内部运行。

WiX 主题 XML、中文本地化文件和旧版主题 PNG 仍保留在仓库中，作为迁移参考，但当前 Bundle
不再加载它们。

对外产品名称为 `Files max`。安装器文件名和解压后的 GitHub Actions 构建产物也使用该名称；
`FilesDev` 身份包名称和 `Files.exe` 可执行文件名保留不变，这是为了兼容现有的 Windows
文件关联、升级和外部位置身份注册。

备用 NSIS 脚本同样使用 NSIS 官方的 Modern UI 2 和简体中文资源，保留为兼容/回滚链路。

选择 Custom Bootstrapper Application 而不是继续堆叠 WiX 主题资源，是为了同时保留 Burn 已有
的 Win11 安装、修复、卸载和外部位置身份注册能力，并让窗口外观和交互真正由项目代码控制。

GitHub Actions 会在构建时提供发布目录和签名的外部位置身份包。Burn 的 `InstallFolder` 变量
会持久化，并以 `INSTALLFOLDER` 传给 MSI，因此安装选项页中选择的路径会同时用于 MSI 和身份
注册脚本。

MSI major upgrade 必须使用 `Schedule="afterInstallExecute"`：先在同一 `FilesDev` 包身份下更新
外部位置身份包，再移除旧 MSI。Windows 会在同身份的包更新时保留应用数据，但注销包身份会清理
`LocalState`；旧 MSI 自带的版本范围卸载逻辑会识别并保留已经更新的新身份包。不要把移除旧 MSI
提前到新身份包注册之前，否则升级会被当成卸载后重装并丢失用户数据。进度页依 MSI 实际动作显示
“安装新版”和“卸载旧版本”两个阶段。

备用 NSIS 脚本保留在 `.github/installer` 中，作为兼容和回滚参考；WiX bundle 工作流不再使用
这两个脚本。
