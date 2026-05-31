# IClassMobile

IClassMobile 是一个面向北航 iClass 课程日历与签到流程的 .NET MAUI 移动客户端。

## 开源来源与归属

本项目基于开源项目 [zeroduhyy/iclass_buaa](https://github.com/zeroduhyy/iclass_buaa) 的 iClass / WebVPN 接入思路进行移动端改造。

上游项目采用 MIT License。根据 MIT License 要求，本仓库保留上游版权和许可声明，详见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。

## 当前状态

- 当前版本：1.1.0
- 主要目标平台：Android
- 当前支持模式：iClass 直连 / WebVPN 登录

## 已知限制

直连模式保留学号输入入口；若学校接口不再接受学号作为 iClass 登录名，请使用 WebVPN 模式完成统一认证后进入课程页。

学校服务器目前不支持补签，请在上课时间内完成签到。网络波动可能导致直连或 WebVPN 签到偶发失败，请确认页面显示成功后再离开。

## 构建

```powershell
dotnet build IClassMobile.csproj -f net10.0-android
```

APK 构建产物不纳入 git 主分支；如需分发，请使用 GitHub Releases。
