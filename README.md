# IClassMobile

IClassMobile 是一个由AI开发的，个人自用的课程签到 .NET MAUI 移动客户端。

本项目主要用于个人学习，不是学校官方应用，也不提供任何稳定服务承诺。

## 开源来源与归属

本项目基于开源项目 [zeroduhyy/iclass_buaa](https://github.com/zeroduhyy/iclass_buaa) 的 iClass / WebVPN 接入思路进行移动端改造，并参考 [BUAASubnet/UBAA](https://github.com/BUAASubnet/UBAA) 最新源码中的统一认证、`loginName` 解析与签到请求流程修复。

上游项目采用 MIT License。根据 MIT License 要求，本仓库保留上游版权和许可声明，详见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。

## 当前状态

- 当前版本：1.2.1
- 主要目标平台：Android
- 当前支持模式：直连登录
- 账号密码仅用于本机与学校系统通信，不会写入源码或提交到仓库。

## 已知限制

直连模式会先完成统一认证，再通过 iClass MyCenter 跳转解析 `loginName`，最后进入课程与签到接口。

请在校园网环境下使用，WebVPN 模式当前不可用

学校服务器目前不支持补签，请在上课时间内完成签到。

## 构建

```powershell
dotnet build IClassMobile.csproj -f net10.0-android
```

APK 构建产物不纳入 git 主分支；如需分发，请使用 GitHub Releases。
