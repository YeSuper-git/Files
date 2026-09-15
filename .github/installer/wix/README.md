# Files max 安装器

安装器继续使用 WiX Toolset 7 的 MSI + Burn 组合：

- `Files max Installer.msi` 负责应用文件、快捷方式、资源管理器右键集成、身份注册、修复和卸载。
- `Files max Setup.exe` 负责用户可见的安装界面、运行库前置依赖、升级检测、缓存和安装器卸载入口。

GitHub Actions 的 Files max 发布产物现在只有一个 `Files max Setup.exe`。旧的
`Files max Setup.version.json` 不再随安装包发布：产品名和版本由 Burn Bundle 本身携带，
更新检查从 GitHub Release 资产的 `digest` 读取 SHA-256，并在启动安装器前重新校验下载文件。
更新代码仍兼容历史 Release 中的 JSON 文件，因此升级链路不会因为移除附属文件而中断。

当前 Burn 界面使用 WiX 官方的 `hyperlinkLargeLicense` 主题，并通过
`Files-Setup.zh-CN.wxl` 提供简体中文文本。主题只是更换 UI 资源，安装目录变量、MSI
链路、修复、升级和卸载逻辑保持不变；安装位置仍可在“选项”页中选择。

对外产品名称为 `Files max`。安装器文件名和解压后的 GitHub Actions 构建产物也使用该名称；
`FilesDev` 身份包名称和 `Files.exe` 可执行文件名保留不变，这是为了兼容现有的 Windows
文件关联、升级和外部位置身份注册。

备用 NSIS 脚本同样使用 NSIS 官方的 Modern UI 2 和简体中文资源，保留为兼容/回滚链路。

选择 WiX 官方主题而不是替换安装引擎，是为了保留当前已经验证过的 Win11 安装、修复、卸载和
外部位置身份注册能力。若后续要做到 WinUI 3 级别的全定制交互，需要另写 Custom Bootstrapper
Application；那会改变 UI 宿主和测试面，不应和本次中文化一起引入。

GitHub Actions 会在构建时提供发布目录和签名的外部位置身份包。Burn 的 `InstallFolder` 变量
会持久化，并以 `INSTALLFOLDER` 传给 MSI，因此安装选项页中选择的路径会同时用于 MSI 和身份
注册脚本。

备用 NSIS 脚本保留在 `.github/installer` 中，作为兼容和回滚参考；WiX bundle 工作流不再使用
这两个脚本。
