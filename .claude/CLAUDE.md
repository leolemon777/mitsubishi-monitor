# MitsubishiMonitor 项目规则

## 项目架构
- MVVM，.NET 8 WPF，4 台三菱 FX3U PLC 监控（MC 协议，HslCommunication）
- 社区工具包 MVVM 源生成器，LiveCharts 图表，EF Core SQLite，EPPlus 导出

## 项目特殊约定（优先于全局 WPF 规范）
- `nullable disable` — 项目全局关闭了 nullable，不要加 nullable 注解
- 无 DI 容器 — Microsoft.Extensions.DependencyInjection 在 csproj 但未启用，服务手动构造
- 所有设备配置硬编码在 DeviceManagerService 中
- PlcStatus 手动实现 INPC（非 CommunityToolkit），因为 X/Y/M 数组需要逐点通知
- 三菱八进制地址：X/Y 用八进制编号，由 PlcConfig.GetXAddress() 处理
- LogBufferService 写缓冲：ConcurrentQueue + 3 秒批量刷写
- UI 事件节流：DeviceDetailViewModel 500ms 刷 pendingLogs

## 全局 WPF 红线规范
遵守 ~/.claude/CLAUDE.md 中的 32 条 WPF 红线规则。
