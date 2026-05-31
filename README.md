# IClassMobile

IClassMobile 是一个面向北航 iClass 课程日历与签到流程的 .NET MAUI 移动客户端。

## 开源来源与归属

本项目基于开源项目 [zeroduhyy/iclass_buaa](https://github.com/zeroduhyy/iclass_buaa) 的 iClass / WebVPN 接入思路进行移动端改造。

上游项目采用 MIT License。根据 MIT License 要求，本仓库保留上游版权和许可声明，详见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。

## 当前状态

- 当前版本：1.0.0
- 主要目标平台：Android
- 当前支持模式：iClass 直连
- 当前不可用：WebVPN / VPN 链接模式

## 已知限制

WebVPN / VPN 链接模式当前不可用。应用界面中已禁用 VPN 入口，请使用直连模式。

## 构建

```powershell
dotnet build IClassMobile.csproj -f net10.0-android
```

APK 构建产物不纳入 git 主分支；如需分发，请使用 GitHub Releases。
